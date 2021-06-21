module Target

open Typer
open Syntax
open Yargs

type GlobalOptions = {|
  verbose: bool
|}

type ITarget<'Options> =
  abstract Command: string
  abstract Description: string
  abstract Builder:(Argv<GlobalOptions> -> Argv<'Options>)
  abstract Run: context:Context * sources:SourceFile list * options:'Options -> unit

open Fable.Core

let register (parse: string[] -> Context * SourceFile list) (target: ITarget<'Options>) (argv: Argv<GlobalOptions>) =
  argv.command(
    U2.Case1 (target.Command + " <inputs..>"),
    target.Description,
    builder = U3.Case2 (fun argv ->
      (target.Builder argv).positional("inputs", {| describe = ".d.ts files to generate a binding" |})
    ),
    handler = (fun (argv: Arguments<'Options>) ->
      let inputs = argv.["inputs"] :?> string[]
      let ctx, srcs = parse inputs
      target.Run(ctx, srcs, argv.Options)))
  |> box
  :?> Argv<GlobalOptions>