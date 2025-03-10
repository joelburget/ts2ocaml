module JsHelper
open Fable.Core
open Fable.Core.JsInterop

module Node = Node.Api

let getPackageJsonPath (exampleFilePath: string) =
  let parts =
    exampleFilePath
    |> String.split Node.path.sep
    |> List.ofArray
  match parts |> List.tryFindIndexBack ((=) "node_modules") with
  | None -> None
  | Some i ->
    let prefix, rest = List.splitAt (i+1) parts
    if rest = [] then None
    else
      let packageName =
        match rest with
        | userName :: packageName :: _ when userName.StartsWith("@") -> [userName; packageName]
        | packageName :: _ -> [packageName]
        | _ -> failwith "impossible_getPackageJsonPath_root"
      let path =
        prefix @ packageName @ ["package.json"] |> String.concat Node.path.sep

      if not <| Node.fs.existsSync(!^path) then None
      else if Node.path.isAbsolute(path) then Some path
      else Some (Node.path.resolve(path))

type IPackageExportItem =
  [<EmitIndexer>]
  abstract Item: string -> string with get

type IPackageJson =
  abstract name: string
  abstract version: string
  abstract types: string option
  abstract typings: string option
  abstract exports: obj option

let getPackageJson (path: string) : IPackageJson =
  let content = Node.fs.readFileSync(path, "utf-8")
  !!JS.JSON.parse(content)

let getPackageInfo (exampleFilePath: string) : Syntax.PackageInfo option =
  match getPackageJsonPath exampleFilePath with
  | None -> None
  | Some path ->
    let p = getPackageJson path

    let rootPath = Node.path.dirname(path)

    let name =
      if p.name.StartsWith("@types/") then
        let tmp = p.name.Substring(7)
        if tmp.Contains("__") then "@" + tmp.Replace("__", "/")
        else tmp
      else p.name

    let shortName =
      p.name
      |> String.splitThenRemoveEmptyEntries "/"
      |> Array.skipWhile (fun s -> s.StartsWith("@"))
      |> String.concat "/"

    let exports =
      match p.exports with
      | None -> []
      | Some exports ->
        [ for k, v in JS.Constructors.Object.entries exports do
            if isIn "types" v then yield k, v?types ]

    let indexFile =
      match Option.orElse p.types p.typings, exports |> List.tryFind (fst >> (=) ".") with
      | None, None ->
        let index = Node.path.join(rootPath, "index.d.ts")
        if not <| Node.fs.existsSync(!^index) then None
        else
          Node.path.relative(Node.``process``.cwd(), index) |> Node.path.normalize |> Some
      | Some typings, _
      | None, Some (_, typings) ->
        Node.path.relative(Node.``process``.cwd(), Node.path.join(rootPath, typings)) |> Node.path.normalize |> Some

    let exports =
      exports
      |> List.filter (fst >> (<>) ".")
      |> List.map (fun (k, v) ->
        {| submodule = k;
           file = Node.path.relative(Node.``process``.cwd(), Node.path.join(rootPath, v)) |> Node.path.normalize |})

    Some {
      name = name
      shortName = shortName
      isDefinitelyTyped = p.name.StartsWith("@types/")
      version = p.version
      rootPath = rootPath
      indexFile = indexFile
      exports = exports
    }

type InferenceResult =
  | Valid of string
  | Heuristic of string
  | Unknown
module InferenceResult =
  let unwrap defaultValue = function
    | Valid s | Heuristic s -> s
    | Unknown -> defaultValue

let inferPackageInfoFromFileName (sourceFile: Path.Relative) : {| name: string; isDefinitelyTyped: bool; rest: string list |} option =
  let parts =
    sourceFile
      |> fun x ->
        let inm = x.LastIndexOf "node_modules"
        if inm = -1 then x
        else x.Substring(inm+13)
      |> String.split "/"
      |> List.ofArray
  match parts with
  | [] -> None
  | "@types" :: name :: rest ->
    let name = if name.Contains("__") then "@" + name.Replace("__", "/") else name
    Some {| name = name; isDefinitelyTyped = true; rest = rest |}
  | user :: name :: rest when user.StartsWith("@") ->
    Some {| name = user + "/" + name; isDefinitelyTyped = true; rest = rest |}
  | name :: rest ->
    Some {| name = name; isDefinitelyTyped = true; rest = rest |}

let inline stripExtension path =
  path |> String.replace ".ts" "" |> String.replace ".d" ""

let getJsModuleName (info: Syntax.PackageInfo option) (sourceFile: Path.Relative) =
  let getSubmodule rest =
    match List.rev rest with
    | "index.d.ts" :: name :: _ -> name
    | name :: _ -> stripExtension name
    | [] -> failwith "impossible"
  match info with
  | Some info ->
    if info.indexFile |> Option.exists ((=) sourceFile) then
      info.name |> Valid
    else
      // make it relative to the package root directory
      let relativePath = Path.diff info.rootPath (Path.absolute sourceFile)
      if info.isDefinitelyTyped then
        Node.path.join(info.name, stripExtension relativePath) |> Valid
      else
        match info.exports |> List.tryFind (fun x -> x.file = sourceFile) with
        | Some export -> Node.path.join(info.name, export.submodule) |> Valid
        | None -> // heuristic
          let submodule =
            relativePath
            |> String.splitThenRemoveEmptyEntries "/"
            |> List.ofArray
            |> getSubmodule
          Node.path.join(info.name, submodule) |> Heuristic
  | None ->
    match inferPackageInfoFromFileName sourceFile with
    | None -> Unknown
    | Some info ->
      if info.isDefinitelyTyped then
        let rest =
          match List.rev info.rest with
          | "index.d.ts" :: rest -> List.rev rest
          | other :: rest ->
            stripExtension other :: rest |> List.rev
          | [] -> []
        info.name :: rest |> String.concat "/" |> Valid
      else
        match info.rest with
        | ["index.d.ts"] -> Valid info.name
        | rest ->
          info.name + "/" + getSubmodule rest
          |> Heuristic

let deriveModuleName (info: Syntax.PackageInfo option) (srcs: Path.Relative list) =
 match srcs with
  | [] -> failwith "impossible_deriveModuleName"
  | [src] -> getJsModuleName info src
  | srcs ->
    let names =
      srcs
      |> List.choose inferPackageInfoFromFileName
      |> List.map (fun info -> info.name)
      |> List.groupBy id
      |> List.map (fun (name, xs) -> name, List.length xs)
    names |> List.maxBy (fun (_, count) -> count) |> fst |> Heuristic

let deriveOutputFileName
  (opts: #GlobalOptions) (info: Syntax.PackageInfo option) (srcs: Path.Relative list)
  (moduleNameToFileName: string -> string) (whenUnknown: string) =
  let inline log x =
    Log.tracef opts "* the inferred output file name is '%s'" x
    x
  match deriveModuleName info srcs with
  | Valid moduleName -> moduleNameToFileName moduleName |> log
  | Heuristic best -> moduleNameToFileName best |> log
  | Unknown ->
    Log.warnf opts "* the output file name cannot be inferred. '%s' is used instead." whenUnknown
    whenUnknown

let resolveRelativeImportPath (info: Syntax.PackageInfo option) (currentFile: Path.Relative) (path: string) =
  if path.StartsWith(".") then
    let targetPath =
      let path = Node.path.join(Node.path.dirname(currentFile), path)
      if not <| path.EndsWith(".ts") then Node.path.join(path, "index.d.ts")
      else path
    getJsModuleName info targetPath
  else
    Valid path
