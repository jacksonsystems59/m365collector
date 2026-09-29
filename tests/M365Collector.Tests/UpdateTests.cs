using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using M365Collector.Core;
using M365Collector.Storage;
using M365Collector.Updater;
using Xunit;
namespace M365Collector.Tests;
public class UpdateTests
{
    [Theory][InlineData("0.0.2","0.0.1")][InlineData("0.1.0","0.0.99")][InlineData("0.10.0","0.9.9")][InlineData("1.0.0","0.99.99")]
    public void VersionsCompareNumerically(string newer,string older) => Assert.True(SemanticVersion.Parse(newer).CompareTo(SemanticVersion.Parse(older))>0);
    [Theory][InlineData("01.0.0")][InlineData("0.1")][InlineData("1.0.0-preview")][InlineData("garbage")]
    public void NonStableVersionsAreRejected(string version) => Assert.Throws<FormatException>(() => SemanticVersion.Parse(version));
    public static string ReleaseJson(string version="0.0.2",bool prerelease=false,bool draft=false) => JsonSerializer.Serialize(new { draft, prerelease, tag_name="v"+version, body="Changes", html_url="https://github.com/jacksonsystems59/m365collector/releases/tag/v"+version, published_at="2026-09-29T12:00:00Z", assets=new[]{new {name=$"M365Collector-{version}-win-x64.zip",browser_download_url=$"https://github.com/jacksonsystems59/m365collector/releases/download/v{version}/M365Collector-{version}-win-x64.zip"}, new {name=$"M365Collector-{version}-win-x64.zip.sha256",browser_download_url=$"https://github.com/jacksonsystems59/m365collector/releases/download/v{version}/M365Collector-{version}-win-x64.zip.sha256"}} });
    [Fact] public void ParsesExpectedAssetsAndUpdateAvailability() { var release=ReleaseClient.Parse(ReleaseJson()); Assert.True(ReleaseClient.IsUpdate("0.0.1",release)); Assert.False(ReleaseClient.IsUpdate("0.0.2",release)); Assert.False(ReleaseClient.IsUpdate("1.0.0",release)); Assert.False(ReleaseClient.IsUpdate("0.0.1",null)); }
    [Theory][InlineData(true,false)][InlineData(false,true)] public void DraftAndPrereleasesAreIgnored(bool pre,bool draft) => Assert.Null(ReleaseClient.Parse(ReleaseJson(prerelease:pre,draft:draft)));
    [Fact] public void MalformedOrUntrustedReleaseFails() { Assert.ThrowsAny<JsonException>(() => ReleaseClient.Parse("not json")); Assert.Throws<InvalidDataException>(() => ReleaseClient.Parse(ReleaseJson().Replace("github.com","attacker.example"))); }
    [Fact] public async Task NetworkFailureIsReportedWithoutCreatingFalseState()
    {
        using var temp=new Scratch(); using var http=new HttpClient(new Handler(_=>throw new HttpRequestException("offline"))); var client=new ReleaseClient(http,temp.At("cache.json")); await Assert.ThrowsAsync<HttpRequestException>(()=>client.CheckAsync(true,CancellationToken.None)); Assert.False(File.Exists(temp.At("cache.json")));
    }
    [Theory][InlineData(403)][InlineData(429)] public async Task RateLimitsAreHandled(int code) { using var temp=new Scratch(); using var http=new HttpClient(new Handler(_=>new((HttpStatusCode)code))); await Assert.ThrowsAsync<HttpRequestException>(()=>new ReleaseClient(http,temp.At("cache")).CheckAsync(true,CancellationToken.None)); }
    [Fact] public async Task NoReleasesIsValidState() { using var temp=new Scratch(); using var http=new HttpClient(new Handler(_=>new(HttpStatusCode.NotFound))); Assert.Null((await new ReleaseClient(http,temp.At("cache")).CheckAsync(true,CancellationToken.None)).Release); }
    [Fact] public async Task EtagAndCacheAvoidUnnecessaryRequests()
    {
        using var temp=new Scratch(); var calls=0; using var http=new HttpClient(new Handler(request=>
        {
            Assert.Contains("M365Collector",request.Headers.UserAgent.ToString()); calls++;
            if(calls==1) { var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(ReleaseJson())}; response.Headers.ETag=new("\"version-1\""); return response; }
            Assert.Equal("\"version-1\"",request.Headers.IfNoneMatch.Single().Tag); return new(HttpStatusCode.NotModified);
        }));
        var client=new ReleaseClient(http,temp.At("cache")); var first=await client.CheckAsync(false,CancellationToken.None); await client.CheckAsync(false,CancellationToken.None); Assert.Equal(1,calls); var next=await client.CheckAsync(true,CancellationToken.None); Assert.Equal(first.Release,next.Release); Assert.Equal(2,calls);
    }
    [Fact] public void ChecksumRejectsCorruptionAndWrongFileName()
    {
        using var temp=new Scratch(); var request=Package(temp.At("work")); UpdatePackage.VerifyChecksum(request.Zip,request.Checksum); File.AppendAllText(request.Zip,"corrupt"); Assert.Throws<InvalidDataException>(()=>UpdatePackage.VerifyChecksum(request.Zip,request.Checksum)); File.WriteAllText(request.Checksum,"invalid"); Assert.Throws<InvalidDataException>(()=>UpdatePackage.VerifyChecksum(request.Zip,request.Checksum));
    }
    [Theory][InlineData("../escape.exe")][InlineData("GUI/../../escape.exe")][InlineData("GUI\\escape.dll")][InlineData("GUI/C:/evil.exe")][InlineData("Service/run.ps1")][InlineData("GUI/NUL.exe")][InlineData("Customers/data.json")][InlineData("/GUI/file.exe")]
    public void UnsafeZipPathsAreRejectedBeforeExtraction(string path)
    {
        using var temp=new Scratch(); var zip=temp.At("bad.zip"); using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){using var writer=new StreamWriter(archive.CreateEntry(path).Open());writer.Write("bad");}
        Assert.Throws<InvalidDataException>(()=>UpdatePackage.Extract(zip,temp.At("extract"),"0.0.2")); Assert.False(Directory.Exists(temp.At("extract")));
    }
    [Fact] public void ValidPackageStagesAndRejectsWrongVersion()
    {
        using var temp=new Scratch(); var request=Package(temp.At("work")); var manifest=UpdatePackage.Extract(request.Zip,temp.At("stage"),"0.0.2"); Assert.Equal(3,manifest.Files.Count); Assert.True(File.Exists(temp.At("stage/Service/M365Collector.Service.exe"))); Assert.Throws<InvalidDataException>(()=>UpdatePackage.Extract(request.Zip,temp.At("wrong"),"9.0.0"));
    }
    [Fact] public void UnlistedPackagePayloadIsRejected()
    {
        using var temp=new Scratch(); var request=Package(temp.At("work")); using(var archive=ZipFile.Open(request.Zip,ZipArchiveMode.Update)){using var writer=new StreamWriter(archive.CreateEntry("GUI/extra.exe").Open());writer.Write("bad");} Assert.Throws<InvalidDataException>(()=>UpdatePackage.Extract(request.Zip,temp.At("out"),"0.0.2"));
    }
    [Theory][InlineData("migration")][InlineData("health")][InlineData("start")][InlineData("none")]
    public async Task TransactionPreservesRuntimeDataAndRollsBackFailures(string failure)
    {
        using var temp=new Scratch(); var store=temp.Database(); store.Setting("preserved","original"); var paths=new RuntimePaths(temp.At("data")); File.WriteAllText(Path.Combine(paths.Root,"Reports","report.txt"),"keep"); File.WriteAllText(Path.Combine(paths.Root,"Config","settings.json"),"original");
        Directory.CreateDirectory(temp.At("app")); File.WriteAllText(temp.At("app/old.exe"),"old binary"); var request=Package(temp.At("work")); var service=new FakeService(failure);
        var transaction=new UpdateTransaction(new(paths.Root,temp.At("app"),true),temp.At("work"),service,ct=>
        {
            store.Setting("preserved","new"); File.WriteAllText(Path.Combine(paths.Root,"Config","settings.json"),"new"); if(failure=="migration")throw new IOException("migration failed"); return Task.CompletedTask;
        });
        if(failure=="none") { await transaction.ApplyAsync(request,CancellationToken.None); Assert.False(File.Exists(temp.At("app/old.exe"))); Assert.Equal("Complete",JsonFile.Read<UpdateJournal>(temp.At("work/journal.json")).State); }
        else { await Assert.ThrowsAsync<IOException>(()=>transaction.ApplyAsync(request,CancellationToken.None)); Assert.Equal("old binary",File.ReadAllText(temp.At("app/old.exe"))); Assert.Equal("original",store.Setting("preserved")); Assert.Equal("original",File.ReadAllText(Path.Combine(paths.Root,"Config","settings.json"))); Assert.Equal("RolledBack",JsonFile.Read<UpdateJournal>(temp.At("work/journal.json")).State); }
        Assert.Equal("keep",File.ReadAllText(Path.Combine(paths.Root,"Reports","report.txt"))); Assert.True(service.Running); store.Verify();
    }
    [Fact] public async Task InvalidPackageNeverStopsService()
    {
        using var temp=new Scratch();temp.Database();Directory.CreateDirectory(temp.At("app")); var request=Package(temp.At("work"));File.AppendAllText(request.Zip,"corrupt");var service=new FakeService("none");var transaction=new UpdateTransaction(new(temp.At("data"),temp.At("app"),true),temp.At("work"),service,_=>Task.CompletedTask);await Assert.ThrowsAsync<InvalidDataException>(()=>transaction.ApplyAsync(request,CancellationToken.None));Assert.Equal(0,service.Stops);
    }
    [Fact] public async Task InterruptedReplacementRecoversFromJournal()
    {
        using var temp=new Scratch();var store=temp.Database();var request=Package(temp.At("work"));Directory.CreateDirectory(temp.At("app"));File.WriteAllText(temp.At("app/new.exe"),"incomplete");Directory.CreateDirectory(temp.At("work/previous-binaries"));File.WriteAllText(temp.At("work/previous-binaries/old.exe"),"working");Directory.CreateDirectory(temp.At("work/previous-config"));store.Backup(temp.At("work/previous.db"));JsonFile.Write(temp.At("work/journal.json"),new UpdateJournal("Replacing","0.0.2","0.0.1"));var service=new FakeService("none");await Assert.ThrowsAsync<IOException>(()=>new UpdateTransaction(new(temp.At("data"),temp.At("app"),true),temp.At("work"),service,_=>Task.CompletedTask).ApplyAsync(request,CancellationToken.None));Assert.Equal("working",File.ReadAllText(temp.At("app/old.exe")));Assert.True(service.Running);
    }
    public static UpdateRequest Package(string work)
    {
        Directory.CreateDirectory(work);var zip=Path.Combine(work,"M365Collector-0.0.2-win-x64.zip");var files=new Dictionary<string,byte[]>();foreach(var component in new[]{"GUI","Service","Updater"})files[component+"/M365Collector."+component+".exe"]=System.Text.Encoding.UTF8.GetBytes("new "+component);
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create))
        {
            foreach(var (name,bytes) in files){using var stream=archive.CreateEntry(name).Open();stream.Write(bytes);}
            using var manifest=archive.CreateEntry("package.json").Open();JsonSerializer.Serialize(manifest,new PackageManifest("0.0.2",files.ToDictionary(f=>f.Key,f=>Convert.ToHexString(SHA256.HashData(f.Value)))));
        }
        File.WriteAllText(zip+".sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)))+"  "+Path.GetFileName(zip));return new(zip,zip+".sha256","0.0.2","0.0.1",0);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request)); }
    private sealed class FakeService(string fail):IServiceControl
    {
        public bool Running=true;public int Stops;private bool failed;
        public Task StopAsync(CancellationToken ct){Stops++;Running=false;return Task.CompletedTask;}
        public Task StartAsync(CancellationToken ct){if(fail=="start"&&!failed){failed=true;throw new IOException("start failed");}Running=true;return Task.CompletedTask;}
        public Task VerifyAsync(RuntimePaths paths,string version,DateTimeOffset since,CancellationToken ct){if(fail=="health"&&!failed){failed=true;throw new IOException("heartbeat failed");}return Task.CompletedTask;}
    }
}
