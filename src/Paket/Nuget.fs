﻿module Paket.Nuget

open System
open System.IO
open System.Net
open System.Xml
open Newtonsoft.Json

let private get (url : string) = 
    async { 
        use client = new WebClient()

        try 
            return! client.AsyncDownloadString(Uri(url))
        with exn -> 
            // TODO: Handle HTTP 404 errors gracefully and return an empty string to indicate there is no content.
            return ""
    }


/// Gets versions of the given package.
let getAllVersions nugetURL package = 
    async { 
        let! raw = sprintf "%s/package-versions/%s" nugetURL package |> get
        if raw = "" then return Seq.empty
        else return JsonConvert.DeserializeObject<string []>(raw) |> Array.toSeq
    }

let parseVersionRange (text:string) = 
    if text = "" then Latest else
    if text.StartsWith "[" then
        if text.EndsWith "]" then 
            VersionRange.Exactly(text.Replace("[","").Replace("]",""))
        else
            let parts = text.Replace("[","").Replace(")","").Split ','
            VersionRange.Between(parts.[0],parts.[1])
    else VersionRange.AtLeast(text)

/// Gets all dependencies of the given package version.
let getDependencies nugetURL package version = 
    async { 
        // TODO: this is a very very naive implementation
        let! raw = sprintf "%s/Packages(Id='%s',Version='%s')/Dependencies" nugetURL package version |> get
        let doc = XmlDocument()
        doc.LoadXml raw
        let manager = new XmlNamespaceManager(doc.NameTable)
        manager.AddNamespace("ns", "http://www.w3.org/2005/Atom")
        manager.AddNamespace("d", "http://schemas.microsoft.com/ado/2007/08/dataservices")
        manager.AddNamespace("m", "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata")
        let packages = 
            seq { 
                for node in doc.SelectNodes("//d:Dependencies", manager) do
                    yield node.InnerText
            }
            |> Seq.head
            |> fun s -> s.Split([| '|' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun d -> d.Split ':')
            |> Array.filter (fun d -> Array.isEmpty d
                                      |> not && d.[0] <> "")
            |> Array.map (fun a -> 
                   a.[0], 
                   if a.Length > 1 then a.[1]
                   else "")
            |> Array.map (fun (name, version) -> 
                   { Name = name
                     // TODO: Parse nuget version ranges - see http://docs.nuget.org/docs/reference/versioning
                     VersionRange = parseVersionRange version
                     SourceType = "nuget"
                     Source = nugetURL })
            |> Array.toList
        return packages
    }
    
let CacheFolder = 
    let appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    Path.Combine(Path.Combine(appData, "NuGet"), "Cache")

let DownloadPackage(source, name, version) = 
    async { 
        let targetFileName = Path.Combine(CacheFolder,name + "." + version + ".nupkg")
        let fi = FileInfo targetFileName
        if fi.Exists && fi.Length > 0L then 
            tracefn "%s %s already downloaded" name version
            return targetFileName 
        else
            let url = 
                match source with
                | "http://nuget.org/api/v2" -> sprintf "http://packages.nuget.org/v1/Package/Download/%s/%s" name version
                | _ -> 
                    // TODO: How can we discover the download link?
                    failwithf "unknown package source %s - can't download package %s %s" source name version
        
            let client = new WebClient()
            tracefn "Downloading %s %s" name version
            // TODO: Set credentials
            client.DownloadFileAsync(Uri url, targetFileName)
            let! _ = Async.AwaitEvent(client.DownloadFileCompleted)
            tracefn "Finished %s %s" name version
            return targetFileName
    }

let NugetDiscovery = 
    { new IDiscovery with
          member __.GetDirectDependencies(sourceType, source, package, version) = 
              if sourceType <> "nuget" then failwithf "invalid sourceType %s" sourceType
              getDependencies source package version
          
          member __.GetVersions(sourceType, source, package) = 
              if sourceType <> "nuget" then failwithf "invalid sourceType %s" sourceType
              getAllVersions source package }