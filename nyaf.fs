open System
open System.IO
open System.Security
open System.Text.Json
open System.Diagnostics

let start = DateTime.Now

let (/+) l r = Path.Combine(l, r)

let prettySerializeopts=
    JsonSerializerOptions(
        WriteIndented = true
    )

let cacheDir = Environment.ExpandEnvironmentVariables "%localappdata%" /+ "nyaf" /+ "cache"

// TODO: setup runtime config template and FSharp.Core.dll
let cacheFile = cacheDir /+ "cache.json"
if not (Directory.Exists cacheDir) then
        Directory.CreateDirectory cacheDir
        |> ignore

let usage = "usage: nyaf <options> <fsx script> <script args>"

type GlobalDotnetOptions = {
    sdk: {| version: string |}
}

let globalDotnetOptions = 
    if File.Exists "global.json" then
        "global.json"
        |> File.ReadAllText
        |> JsonSerializer.Deserialize<GlobalDotnetOptions>
    // TODO: discover sdk version
    else { sdk = {| version = "7.0.100" |} }

type Options = {
    script: string 
    forceRebuild: bool
    buildOnly: bool
    passedArgs: string list
    verbose: bool
    noMangle: bool
    outputDir: string option
}
    with static member Default = {
            script = ""
            forceRebuild = false
            buildOnly = false
            passedArgs = []
            verbose = false
            noMangle = false
            outputDir = None
        }

type ScriptCache = {
    pathToSrc: string
    srcHash: string
}

let rec parseOptions opts args =
    match args with
    | [] ->
        match opts.script with
        | "" -> Error "No script specified"
        | _ -> Ok opts
    | ("--force"|"-f")::rest ->
        parseOptions { opts with forceRebuild = true } rest
    | ("--build-only"|"-b")::rest ->
        parseOptions { opts with buildOnly = true } rest
    | ("--verbose"|"-v")::rest ->
        parseOptions { opts with verbose = true } rest
    // | ("--no-mangle"|"-n")::rest ->
    //     printfn "Mangling disabled"
    //     parseOptions { opts with noMangle = true } rest
    | ("--output"|"-o")::path::rest ->
        parseOptions { opts with outputDir = Some path; noMangle = true } rest
    | path::rest 
        when path.EndsWith ".fsx" ->
        if File.Exists path then
            printfn $"Target script path: {path}" 
            Ok { opts with script = path; passedArgs = rest }
        else
            Error $"File not found: '{path}'"
    | path::rest 
        when File.Exists (path+".fsx") ->
        printfn $"Target script path: {path}" 
        Ok { opts with script = path+".fsx"; passedArgs = rest }
    | unknown::_ ->
        Error $"unknown option '{unknown}'"

let clargs = Environment.GetCommandLineArgs()  |> Array.skip 1
let opts = parseOptions Options.Default (clargs |> List.ofArray)

let startupDone = DateTime.Now

match opts with
| Ok opts ->
    let printIfVerbose = if opts.verbose then printfn "%s" else ignore
    printIfVerbose $"startup time: {startupDone - start}"
    printIfVerbose ("Args: " + String.Join(" ", clargs))
    printIfVerbose ("Args to pass on: " + String.Join(" ", opts.passedArgs))
    let caches =
        if not (File.Exists cacheFile) then
            Map.empty
        else
            cacheFile
            |> File.ReadAllText
            |> JsonSerializer.Deserialize<ScriptCache array>
            |> Array.map (fun c -> c.pathToSrc, c)
            |> Map.ofArray


    let script = Path.GetFullPath "." /+ opts.script

    let hashFile = File.ReadAllBytes  >> Cryptography.SHA256.HashData >> BitConverter.ToString >> fun s -> s.Replace("-", "")
    
    let thisHash = 
        if opts.noMangle then
            opts.script.Replace(".fsx", "")
        else
            let hash = hashFile script
            hash

    let outputDir = 
        match opts.outputDir with
        | Some path -> path
        | None -> cacheDir

    let exe = outputDir /+ $"{thisHash}.exe"
    let cfg = outputDir /+ $"{thisHash}.runtimeconfig.json"

    let cache' = Map.tryFind script caches
    let cacheDone = DateTime.Now
    printIfVerbose $"cache load time: {cacheDone - startupDone}"
    match cache' with
    | Some cache when not opts.forceRebuild && cache.srcHash = thisHash ->
        printIfVerbose "found cached version"
    | x ->
        match x with
        | Some outdated ->
            printIfVerbose "deleting out of date exe and config"
            File.Delete (outputDir /+ $"{outdated.srcHash}.exe")
            File.Delete (outputDir /+ $"{outdated.srcHash}.runtimeconfig.json")
        | _ -> ()
        printIfVerbose "cache was out of date, building"
        let sdkpath = "C:/Program Files/dotnet/sdk" /+ globalDotnetOptions.sdk.version
        let buildargs = [
            "\"" + sdkpath /+ "FSharp/fsc.dll" + "\""
            "--targetprofile:netcore"
            "--langversion:6.0"
            if not opts.verbose then "--nologo"
            $"--out:{exe}"
            script
        ]
        let buildProc = Process.Start("dotnet", String.Join(" ", buildargs))
        buildProc.WaitForExit()
        if buildProc.ExitCode <> 0 then
            printIfVerbose "build failed"
            Environment.Exit buildProc.ExitCode
        else
            printIfVerbose "build succeeded"
        File.Copy(cacheDir /+ "runtimeconfig.json", cfg)
        let buildDone = DateTime.Now
        printIfVerbose $"build time: {buildDone - cacheDone}"
        let cache = {
            pathToSrc = script
            srcHash = thisHash
        }
        Map.change script (fun _ -> Some cache) caches
        // |> Map.toSeq |> Seq.map snd
        |> Map.values
        |> Array.ofSeq
        |> fun o -> JsonSerializer.Serialize(o, prettySerializeopts)
        |> fun s -> File.WriteAllText(cacheFile, s)
        printIfVerbose "saved new cache"
        let newCacheTime = DateTime.Now
        printIfVerbose $"cache save time: {newCacheTime - buildDone}"
    if not opts.buildOnly then
        let runProc = Process.Start("dotnet", Array.ofList (exe::opts.passedArgs))
        runProc.WaitForExit()
        exit runProc.ExitCode
| Error msg ->
    Console.ForegroundColor <- ConsoleColor.Red
    Console.Write $"error: {msg}\n"
    Console.ResetColor()
    printfn $"{usage}"
    exit 1