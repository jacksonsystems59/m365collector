using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.GUI;
using Xunit;

namespace M365Collector.Tests;

public sealed class IntegrationTests
{
    [Fact] public async Task ServiceProcessMaintainsHeartbeatAndProcessesInvalidDraftWithoutActivatingIt()
    {
        using var runtime=new TestRuntime();
        var executableDirectory=Path.GetDirectoryName(typeof(M365Collector.Service.Program).Assembly.Location)!;
        new ConfigurationStore(runtime.Paths).Save(runtime.Config with { InstallRoot=executableDirectory });
        var customer=runtime.Customer() with { ClientId=Guid.Empty,Certificate=null }; runtime.Db.SaveDraft(customer);
        var id=runtime.Db.Enqueue(customer.TenantId,"Validate");
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(typeof(M365Collector.Service.Program).Assembly.Location); start.ArgumentList.Add("--data-root");start.ArgumentList.Add(runtime.Paths.Root);
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(!File.Exists(runtime.Paths.Health)||runtime.Db.GetJob(id)!.Status is "Queued" or "Running")
            {
                if(process.HasExited)throw new Exception("Service exited: "+await stdout+await stderr);
                await Task.Delay(100,timeout.Token);
            }
            var health=JsonFiles.Read<ServiceHealth>(runtime.Paths.Health);
            Assert.Equal("0.1.0",health.Version);Assert.True(health.IsFresh(DateTimeOffset.UtcNow));Assert.Equal("Running",health.Status);
            Assert.Equal("Failed",runtime.Db.GetJob(id)!.Status);Assert.Equal(CustomerState.Draft,runtime.Db.Find(customer.TenantId)!.State);
            Assert.Throws<IOException>(()=>new FileStream(Path.Combine(runtime.Paths.Root,"Service","runtime.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
            var firstHeartbeat=health.HeartbeatAt;
            while(JsonFiles.Read<ServiceHealth>(runtime.Paths.Health).HeartbeatAt==firstHeartbeat)await Task.Delay(200,timeout.Token);
            Assert.False(process.HasExited);
        }
        finally { if(!process.HasExited) { process.Kill(true);await process.WaitForExitAsync(); } await stdout;await stderr; }
    }
    [Fact] public async Task ServiceVersionDoesNotNeedRuntimeConfiguration()
    {
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true };
        start.ArgumentList.Add(typeof(M365Collector.Service.Program).Assembly.Location);start.ArgumentList.Add("--version");
        using var process=Process.Start(start)!;var output=await process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();
        Assert.Equal(0,process.ExitCode);using var json=JsonDocument.Parse(output);Assert.Equal("0.1.0",json.RootElement.GetProperty("version").GetString());
    }
    [Fact] public void RenderWizardAndManagementViewsOnStaThread()
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            try
            {
                using var runtime=new TestRuntime();
                using var wizard=new FirstRunWizard(); Render(wizard,"first-run");
                using var management=new MainForm(runtime.Config);Render(management,"dashboard");
                management.Navigate("Add Customer");Render(management,"add-customer");
                management.Navigate("Settings");Render(management,"settings");
                management.Navigate("Audit Explorer");Render(management,"audit-placeholder");
            }
            catch(Exception exception) { error=exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        Assert.Null(error);
    }
    private static void Render(Form form,string name)
    {
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;
        form.Show();Application.DoEvents();Layout(form);Application.DoEvents();
        using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
        var directory=Environment.GetEnvironmentVariable("M365COLLECTOR_QA_DIR");
        if(!string.IsNullOrWhiteSpace(directory)) { Directory.CreateDirectory(directory);bitmap.Save(Path.Combine(directory,name+".png"),ImageFormat.Png); }
        Assert.Contains("0.1.0",form.Text);
        Assert.Contains(form.Controls.Cast<Control>(),control=>control.Visible);
    }
    private static void Layout(Control parent) { parent.PerformLayout();foreach(Control child in parent.Controls) { child.CreateControl();Layout(child); } }
    [Fact] public void PackageManifestRejectsTraversalAndCorruptHashes()
    {
        using var runtime=new TestRuntime();var file=Path.Combine(runtime.Paths.Root,"example.txt");File.WriteAllText(file,"package content");
        var manifest=new ReleaseManifest(1,"M365Collector","0.1.0","win-x64","M365Collector.exe","service/M365Collector.Service.exe",1,1,[new("../outside.txt","bad")]);
        JsonFiles.WriteAtomic(Path.Combine(runtime.Paths.Root,"release.json"),manifest);Assert.Throws<InvalidDataException>(()=>ReleasePackage.Verify(runtime.Paths.Root));
        JsonFiles.WriteAtomic(Path.Combine(runtime.Paths.Root,"release.json"),manifest with { Files=[new("example.txt",new string('0',64))] });
        Assert.Throws<InvalidDataException>(()=>ReleasePackage.Verify(runtime.Paths.Root));
    }
}
