module Main

open Fable.Core.JsInterop
open TypeScript
open Node.Api
open Syntax

type ICompilerHost =
  abstract getSourceFile: fileName: string * languageVersion: Ts.ScriptTarget * ?onError: (string -> unit) * ?shouldCreateNewSourceFile: bool -> Ts.SourceFile option
  abstract getSourceFileByPath: fileName: string * path: Ts.Path * languageVersion: Ts.ScriptTarget * ?onError: (string -> unit) * ?shouldCreateNewSourceFile: bool -> Ts.SourceFile option
  abstract getDefaultLibFileName: options: Ts.CompilerOptions -> string
  abstract useCaseSensitiveFileNames: unit -> bool
  abstract getCanonicalFileName: fileName: string -> string
  abstract getCurrentDirectory: unit -> string
  abstract getNewLine: unit -> string
  abstract fileExists: fileName: string -> bool
  abstract readFile: fileName: string -> string option
  abstract directoryExists: directoryName: string -> bool
  abstract getDirectories: path: string -> ResizeArray<string>

let createProgram (tsPaths: string[]) (sourceFiles: Ts.SourceFile list) =
  let options = jsOptions<Ts.CompilerOptions>(fun o ->
    o.target <- Some Ts.ScriptTarget.Latest
    o.``module`` <- Some Ts.ModuleKind.None
    o.incremental <- Some false
    o.checkJs <- Some true
    o.lib <- Some (ResizeArray ["ESNext"; "DOM"])
    o.noEmit <- Some true
    o.alwaysStrict <- Some true
    o.strict <- Some true
    o.skipLibCheck <- Some false
    o.allowJs <- Some true
  )
  let host =
    { new ICompilerHost with
        member _.getSourceFile(fileName, _, ?__, ?___) =
          sourceFiles |> List.tryFind (fun sf -> sf.fileName = fileName)
        member _.getSourceFileByPath(fileName, _, _, ?__, ?___) =
          sourceFiles |> List.tryFind (fun sf -> sf.fileName = fileName)
        member _.getDefaultLibFileName(_) = "lib.d.ts"
        member _.useCaseSensitiveFileNames() = false
        member _.getCanonicalFileName(s) = s
        member _.getCurrentDirectory() = ""
        member _.getNewLine() = "\r\n"
        member _.fileExists(fileName) = Array.contains fileName tsPaths
        member _.readFile(fileName) = sourceFiles |> List.tryPick (fun sf -> if sf.fileName = fileName then Some (sf.getFullText()) else None)
        member _.directoryExists(_) = true
        member _.getDirectories(_) = ResizeArray []
    }
  ts.createProgram(ResizeArray tsPaths, options, !!host)

let expandSourceFiles (opts: GlobalOptions) (sourceFiles: Ts.SourceFile seq) =
  let sourceFilesMap =
    sourceFiles
    |> Seq.map (fun sf -> sf.fileName, sf)
    |> Map.ofSeq

  let expanded =
    sourceFiles
    |> Seq.fold (fun sfMap sf ->

      sfMap) sourceFilesMap

  expanded |> Map.toArray |> Array.map snd

let parse (opts: GlobalOptions) (argv: string[]) : Input =
  let program =
    let inputs = argv |> Seq.map (fun a -> a, fs.readFileSync(a, "utf-8"))
    let srcs =
      inputs |> Seq.map (fun (a, i) ->
        ts.createSourceFile (a, i, Ts.ScriptTarget.Latest, setParentNodes=true))
    createProgram argv (Seq.toList srcs)

  let srcs = program.getSourceFiles()
  let checker = program.getTypeChecker()
  let rec display (node: Ts.Node) depth =
    let indent = String.replicate depth "  "
    System.Enum.GetName(typeof<Ts.SyntaxKind>, node.kind) |> printfn "%s%A" indent
    node.forEachChild(fun child -> display child (depth+1); None) |> ignore

  let sources =
    srcs
    |> Seq.toList
    |> List.map (fun src ->
      Log.tracef opts "* parsing %s..." src.fileName
      let references =
        Seq.concat [
          src.referencedFiles |> Seq.map (fun x -> FileReference x.fileName)
          src.typeReferenceDirectives |> Seq.map (fun x -> TypeReference x.fileName)
          src.libReferenceDirectives |> Seq.map (fun x -> LibReference x.fileName)
        ] |> Seq.toList
      let statements =
        src.statements
        |> Seq.collect (Parser.readStatement !!{| verbose = opts.verbose; checker = checker; sourceFile = src; nowarn = opts.nowarn |})
        |> Seq.toList
      { statements = statements
        fileName = Path.relative src.fileName
        moduleName = src.moduleName
        hasNoDefaultLib = src.hasNoDefaultLib
        references = references })

  let info =
    match sources with
    | example :: _ -> JsHelper.getPackageInfo example.fileName
    | [] -> None

  { sources = sources; info = info }
open Yargs

[<EntryPoint>]
let main argv =
  let yargs =
    yargs
         .Invoke(argv)
         .wrap(yargs.terminalWidth() |> Some)
         .parserConfiguration({| ``parse-positional-numbers`` = false |})
         .config()
    |> GlobalOptions.register
    |> Typer.TyperOptions.register
    |> Target.register parse Targets.JsOfOCaml.Target.target
    |> Target.register parse Targets.ParserTest.target
  yargs.demandCommand(1.0).scriptName("ts2ocaml").help().argv |> ignore
  0
