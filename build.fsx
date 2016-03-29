﻿#I @"packages/FAKE/tools"
#r "FakeLib.dll"

open Fake
open System
open System.IO
open Fake.Testing.XUnit2
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.RestorePackageHelper
open Fake.OpenCoverHelper
open Fake.ProcessHelper
open Fake.AppVeyor

// ------------------------------------------------------------------------------ Project

let buildSolutionFile = "./CodeWriter.sln"
let buildConfiguration = "Release"

type Project = { 
    Name: string;
    Folder: string;
    Template: bool;
    Executable: bool;
    AssemblyVersion: string;
    PackageVersion: string;
    Releases: ReleaseNotes list;
    Dependencies: (string * string) list;
}

let emptyProject = { Name=""; Folder=""; Template=false; Executable=false;
                     AssemblyVersion=""; PackageVersion=""; Releases=[]; Dependencies=[] }

let decoratePrerelease v =
    let couldParse, parsedInt = System.Int32.TryParse(v)
    if couldParse then "build" + (sprintf "%04d" parsedInt) else v

let decoratePackageVersion v =
    if hasBuildParam "nugetprerelease" then
        v + "-" + decoratePrerelease((getBuildParam "nugetprerelease"))
    else
        v

let projects = 
    ([
       {
         emptyProject with Name = "CodeWriter"
                           Folder = "./core/CodeWriter" }
     ]
     |> List.map (fun p -> 
            let parsedReleases = 
                File.ReadLines(p.Folder @@ (p.Name + ".Release.md")) |> ReleaseNotesHelper.parseAllReleaseNotes
            let latest = List.head parsedReleases
            { p with AssemblyVersion = latest.AssemblyVersion
                     PackageVersion = decoratePackageVersion (latest.AssemblyVersion)
                     Releases = parsedReleases }))

let project name =
    List.filter (fun p -> p.Name = name) projects |> List.head

let dependencies p deps =
    p.Dependencies |>
    List.map (fun d -> match d with 
                       | (id, "") -> (id, match List.tryFind (fun (x, ver) -> x = id) deps with
                                          | Some (_, ver) -> ver
                                          | None -> ((project id).PackageVersion))
                       | (id, ver) -> (id, ver))

// ---------------------------------------------------------------------------- Variables

let binDir = "bin"
let testDir = binDir @@ "test"
let nugetDir = binDir @@ "nuget"
let nugetWorkDir = nugetDir @@ "work"

// ------------------------------------------------------------------------------ Targets

Target "Clean" (fun _ -> 
    CleanDirs [binDir]
)

Target "AssemblyInfo" (fun _ ->
    projects
    |> List.filter (fun p -> not p.Template)
    |> List.iter (fun p -> 
        CreateCSharpAssemblyInfo (p.Folder @@ "Properties" @@ "AssemblyInfoGenerated.cs")
          [ Attribute.Version p.AssemblyVersion
            Attribute.FileVersion p.AssemblyVersion
            Attribute.InformationalVersion p.PackageVersion ]
        )
)

Target "RestorePackages" (fun _ -> 
     buildSolutionFile
     |> RestoreMSSolutionPackages (fun p ->
         { p with
             OutputPath = "./packages"
             Retries = 4 })
 )

Target "Build" (fun _ ->
    !! buildSolutionFile
    |> MSBuild "" "Rebuild" [ "Configuration", buildConfiguration ]
    |> Log "Build-Output: "
)

Target "Test" (fun _ -> 
    ensureDirectory testDir
    projects
    |> List.map (fun project -> (project.Folder + ".Tests") @@ "bin" @@ buildConfiguration @@ (project.Name + ".Tests.dll"))
    |> List.filter (fun path -> File.Exists(path))
    |> xUnit2 (fun p -> 
        {p with 
            ToolPath = "./packages/FAKE/xunit.runner.console/tools/xunit.console.exe";
            ShadowCopy = false;
            XmlOutputPath = Some (testDir @@ "test.xml") })
    if not (String.IsNullOrEmpty AppVeyorEnvironment.JobId) then
        UploadTestResultsFile Xunit (testDir @@ "test.xml"))

Target "Cover" (fun _ -> 
    ensureDirectory testDir
    projects
    |> List.map (fun project -> (project.Folder + ".Tests") @@ "bin" @@ buildConfiguration @@ (project.Name + ".Tests.dll"))
    |> List.filter (fun path -> File.Exists(path))
    |> String.concat " "
    |> (fun dlls ->
    OpenCover (fun p -> 
        { p with ExePath = "./packages/OpenCover/tools/OpenCover.Console.exe"
                 TestRunnerExePath = "./packages/FAKE/xunit.runner.console/tools/xunit.console.exe"
                 Output = testDir @@ "coverage.xml"
                 Register = RegisterUser
                 Filter = "+[*]* -[*.Tests]* -[xunit*]*"})
                 (dlls + " -noshadow"))
    if getBuildParam "coverallskey" <> "" then
        // disable printing args to keep coverallskey secret
        ProcessHelper.enableProcessTracing <- false
        let result = ExecProcess (fun info ->
            info.FileName <- "./packages/coveralls.io/tools/coveralls.net.exe"
            info.Arguments <- testDir @@ "coverage.xml" + " -r " + (getBuildParam "coverallskey")) TimeSpan.MaxValue
        if result <> 0 then failwithf "Failed to upload coverage data to coveralls.io"
        ProcessHelper.enableProcessTracing <- true)

let createNugetPackages _ =
    projects
    |> List.iter (fun project -> 
        let nugetFile = project.Folder @@ project.Name + ".nuspec";
        let workDir = nugetWorkDir @@ project.Name;

        let dllFileName = project.Folder @@ "bin/Release" @@ project.Name;
        let dllFiles = (!! (dllFileName + ".dll")
                        ++ (dllFileName + ".pdb")
                        ++ (dllFileName + ".xml"))
        dllFiles |> CopyFiles (workDir @@ "lib" @@ "net45")

        let dllFileNameNet35 = (project.Folder + ".Net35") @@ "bin/Release" @@ project.Name;
        let dllFilesNet35 = (!! (dllFileNameNet35 + ".dll")
                             ++ (dllFileNameNet35 + ".pdb")
                             ++ (dllFileNameNet35 + ".xml"))
        if (Seq.length dllFilesNet35 > 0) then (
            dllFilesNet35 |> CopyFiles (workDir @@ "lib" @@ "net35"))
         
        let isAssemblyInfo f = (filename f).Contains("AssemblyInfo")
        let isSrc f = (hasExt ".cs" f) && not (isAssemblyInfo f)
        CopyDir (workDir @@ "src") project.Folder isSrc

        let packageFile = project.Folder @@ "packages.config"
        let packageDependencies = if (fileExists packageFile) then (getDependencies packageFile) else []

        NuGet (fun p -> 
            {p with
                Project = project.Name
                OutputPath = nugetDir
                WorkingDir = workDir
                Dependencies = dependencies project packageDependencies
                SymbolPackage = (if (project.Template || project.Executable) then NugetSymbolPackage.None else NugetSymbolPackage.Nuspec)
                Version = project.PackageVersion 
                ReleaseNotes = (List.head project.Releases).Notes |> String.concat "\n"
            }) nugetFile
    )

let publishNugetPackages _ =
    projects
    |> List.iter (fun project -> 
        try
            NuGetPublish (fun p -> 
                {p with
                    Project = project.Name
                    OutputPath = nugetDir
                    WorkingDir = nugetDir
                    AccessKey = getBuildParamOrDefault "nugetkey" ""
                    PublishUrl = getBuildParamOrDefault "nugetpublishurl" ""
                    Version = project.PackageVersion })
        with e -> if getBuildParam "forcepublish" = "" then raise e; ()
        if not project.Template && not project.Executable && hasBuildParam "nugetpublishurl" then (
            // current FAKE doesn't support publishing symbol package with NuGetPublish.
            // To workaround thid limitation, let's tweak Version to cheat nuget read symbol package
            try
                NuGetPublish (fun p -> 
                    {p with
                        Project = project.Name
                        OutputPath = nugetDir
                        WorkingDir = nugetDir
                        AccessKey = getBuildParamOrDefault "nugetkey" ""
                        PublishUrl = getBuildParamOrDefault "nugetpublishurl" ""
                        Version = project.PackageVersion + ".symbols" })
            with e -> if getBuildParam "forcepublish" = "" then raise e; ()
        )
    )

Target "Nuget" <| fun _ ->
    createNugetPackages()
    publishNugetPackages()

Target "CreateNuget" <| fun _ ->
    createNugetPackages()

Target "PublishNuget" <| fun _ ->
    publishNugetPackages()

Target "CI" <| fun _ ->
    ()

Target "Help" (fun _ ->  
    List.iter printfn [
      "usage:"
      "build [target]"
      ""
      " Targets for building:"
      " * Build        Build"
      " * Test         Build and Test"
      " * Nuget        Create and publish nugets packages"
      " * CreateNuget  Create nuget packages"
      "                [nugetprerelease={VERSION_PRERELEASE}] "
      " * PublishNuget Publish nugets packages"
      "                [nugetkey={API_KEY}] [nugetpublishurl={PUBLISH_URL}] [forcepublish=1]"
      " * CI           Build, Test and Nuget for CI"
      ""]
)

// --------------------------------------------------------------------------- Dependency

"Clean"
  ==> "AssemblyInfo"
  ==> "RestorePackages"
  ==> "Build"
  ==> "Test"

"Build" ==> "Nuget"
"Build" ==> "CreateNuget"
"Build" ==> "Cover"

"Test" ==> "CI"
"Cover" ==> "CI"
"Nuget" ==> "CI"

RunTargetOrDefault "Help"