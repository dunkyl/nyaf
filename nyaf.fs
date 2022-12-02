open System
open System.IO
open System.Security
open System.Text.Json
open System.Diagnostics
open type System.Environment

let startTime = DateTime.Now

let (/+) l r = Path.Combine(l, r)

let prettySerializeopts=
    JsonSerializerOptions(
        WriteIndented = true
    )

let cacheDir = ExpandEnvironmentVariables "%localappdata%" /+ "nyaf" /+ "cache"

let run exe args =
    let result = Process.Start(new ProcessStartInfo(
        exe, args,
        RedirectStandardOutput = true
    ))
    result.WaitForExit()
    if result.ExitCode <> 0 then
        printfn $"Error running {exe} {args}"
        Console.ForegroundColor <- ConsoleColor.Red
        printfn $"{result.StandardError.ReadToEnd()}"
        Console.ResetColor()
        exit result.ExitCode
    else
        result.StandardOutput.ReadToEnd()

// TODO: setup runtime config template and FSharp.Core.dll
let cacheFile = cacheDir /+ "cache.json"
if not (Directory.Exists cacheDir) then
        Directory.CreateDirectory cacheDir
        |> ignore

let usage = "usage: nyaf <options> <fsx script> <script args>"

let dotnetVersion = (run "dotnet" "--version").Trim()


let findSdkDir (ver: string) =
    let sdk = // like 'X.0.0 [/path/to/sdk]'
        (run "dotnet" "--list-sdks").Split('\n')
        |> Seq.find (fun s -> s.StartsWith ver)
    (sdk.Split(' ')[1]).Trim('[', ']') // just containing folder
    /+ ver

type Options = {
    script: string 
    forceRebuild: bool
    buildOnly: bool
    passedArgs: string list
    verbose: bool
}
    with static member Default = {
            script = ""
            forceRebuild = false
            buildOnly = false
            passedArgs = []
            verbose = false
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

let clargs = GetCommandLineArgs()  |> Array.skip 1
let opts = parseOptions Options.Default (clargs |> List.ofArray)

let startupDone = DateTime.Now

match opts with
| Ok opts ->
    let printIfVerbose = if opts.verbose then printfn "%s" else ignore
    printIfVerbose $"startup time: {startupDone - startTime}"
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

    let hashFile = File.ReadAllBytes >> Cryptography.SHA256.HashData >> BitConverter.ToString >> fun s -> s.Replace("-", "")
    
    let scriptHash = hashFile script

    let exe = cacheDir /+ $"{scriptHash}.exe"
    let cfg = cacheDir /+ $"{scriptHash}.runtimeconfig.json"

    let fsx_tmp = cacheDir /+ $"{scriptHash}.fsx"
    // dotnet 7 issue with fsc needs this imported if used
    // no impact if included twice i think?
    let workaround_text = "#r \"System.Security.Cryptography\"\n"+File.ReadAllText(opts.script)
    File.WriteAllText(fsx_tmp, workaround_text)

    let cache' = Map.tryFind script caches
    let cacheDone = DateTime.Now
    printIfVerbose $"cache load time: {cacheDone - startupDone}"
    match cache' with
    | Some cache when not opts.forceRebuild && cache.srcHash = scriptHash ->
        printIfVerbose "found cached version"
    | x ->
        match x with
        | Some outdated ->
            printIfVerbose "deleting out of date exe and config"
            File.Delete (cacheDir /+ $"{outdated.srcHash}.exe")
            File.Delete (cacheDir /+ $"{outdated.srcHash}.runtimeconfig.json")
        | _ -> ()
        printIfVerbose "cache was out of date, building"
        let sdkpath = findSdkDir dotnetVersion
        let fsharpc = sdkpath /+ "FSharp" /+ "fsc.dll"
        let buildargs = [
            "\"" + fsharpc + "\""
            "--targetprofile:netcore"
            "--langversion:7.0"
            if not opts.verbose then "--nologo"
            $"--out:{exe}"
            fsx_tmp
        ]
        run "dotnet" (String.Join(" ", buildargs)) |> printfn "%s" 
        File.Copy(cacheDir /+ "runtimeconfig.json", cfg)
        let buildDone = DateTime.Now
        printIfVerbose $"build time: {buildDone - cacheDone}"
        let cache = {
            pathToSrc = script
            srcHash = scriptHash
        }
        Map.change script (fun _ -> Some cache) caches
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
    printfn $"error: {msg}\n"
    Console.ResetColor()
    printfn $"{usage}"
    exit 1