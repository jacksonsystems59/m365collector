using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Security;
using M365Collector.Storage;

namespace M365Collector.GUI;

public sealed class FirstRunWizard : Form
{
    private static readonly string[] Steps = ["Welcome", "System requirements", "Dependency check", "Data storage", "Runtime structure", "Windows service", "Application security", "Setup verification"];
    private readonly ListBox navigation = new() { Dock = DockStyle.Left, Width = 235, BorderStyle = BorderStyle.None, BackColor = Ui.Navy, ForeColor = Color.White, ItemHeight = 46, DrawMode = DrawMode.OwnerDrawFixed, Enabled = false };
    private readonly Panel body = new() { Dock = DockStyle.Fill };
    private readonly Button back = Ui.Button("Back"), next = Ui.Button("Next", primary: true), cancel = Ui.Button("Cancel");
    private readonly Label status = Ui.Text("");
    private readonly CancellationTokenSource lifetime = new();
    private RuntimeConfig? config;
    private string root;
    private int step;
    private bool dependenciesPassed, storageChecked, busy;
    private IReadOnlyList<DependencyResult> dependencyResults = [];
    public RuntimeConfig? CompletedConfig { get; private set; }

    public FirstRunWizard(string? existingRoot = null)
    {
        Text = "M365Collector " + ProductInfo.Version + " | First Run"; Width = 1180; Height = 820; MinimumSize = new Size(1000, 700);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Ui.Canvas;
        root = existingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "M365Collector");
        if (existingRoot != null) config = new ConfigurationStore(new RuntimePaths(existingRoot)).Load();
        foreach (var (name, index) in Steps.Select((name,index)=>(name,index))) navigation.Items.Add($"{index+1:00}   {name}");
        navigation.DrawItem += (_, e) => { if(e.Index<0)return; using var brush=new SolidBrush(e.Index == step ? Ui.Teal : Ui.Navy); e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, navigation.Items[e.Index].ToString(), Font, new Point(e.Bounds.X+16,e.Bounds.Y+14), Color.White); };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 68, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(18,12,18,8), BackColor = Color.White };
        footer.Controls.AddRange([next, back, cancel]);
        var header = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(25,17,0,0), Text = "M365COLLECTOR   /   FIRST RUN                                        v" + ProductInfo.Version, ForeColor = Color.White, BackColor = Ui.Navy, Font = new Font("Segoe UI", 15, FontStyle.Bold) };
        Controls.Add(body); Controls.Add(navigation); Controls.Add(footer); Controls.Add(header);
        back.Click += (_,_) => { if (!busy && step>0) { step--; Render(); } };
        next.Click += async (_,_) => await Advance(); cancel.Click += (_,_) => Close();
        FormClosing += (_,e) => { if (busy) { e.Cancel = true; status.Text = "Please wait for the current setup operation to finish."; } else lifetime.Cancel(); };
        Render();
    }
    private void Render()
    {
        body.Controls.Clear(); navigation.Invalidate();
        var page = Ui.Page(Steps[step], $"Step {step+1} of {Steps.Length} · M365Collector {ProductInfo.Version}"); body.Controls.Add(page);
        back.Enabled = step > 0 && config == null; next.Text = step == 7 ? "Finish & add customer" : "Next";
        status.Text = ""; status.ForeColor = Ui.Teal;
        switch(step)
        {
            case 0:
                page.Controls.Add(Ui.Text("A secure foundation for every customer tenant.",18,true));
                page.Controls.Add(Ui.Text("Set up storage, install the independent collection service, and prepare certificate-based Microsoft 365 onboarding.\n\nYour data stays in a folder you choose. Closing the management application never stops the service.\n\nThis release collects tenant identity only. No automatic update system is included."));
                if(config != null) page.Controls.Add(Ui.Text("An incomplete setup was found. We will resume using its registered DataRoot."));
                break;
            case 1:
                page.Controls.Add(Ui.Text("Windows Server 2019 or later / Windows 11 · x64\nLocal NTFS storage and an elevated Windows administrator\nOutbound HTTPS: login.microsoftonline.com and graph.microsoft.com\nSelf-contained .NET runtime supplied in the release package\nPowerShell is optional for the Tenant Identity collector."));
                page.Controls.Add(Ui.Text("No PowerShell modules will be installed. Service binaries are placed under Program Files; customer data remains under your selected DataRoot.")); break;
            case 2:
                page.Controls.Add(Ui.Button("Run dependency checks", async (_,_) => await Check(page), true));
                page.Controls.Add(Ui.Text("Required checks must pass before continuing. No software is installed by this check.")); break;
            case 3:
                var field = Ui.Field(page, "Where should M365Collector store its data?", root, config != null);
                field.TextChanged += (_,_) => { root=field.Text; storageChecked=false; };
                var browse = Ui.Button("Browse…", (_,_) => { using var picker=new FolderBrowserDialog { Description="Choose a dedicated empty data folder", UseDescriptionForTitle=true }; if(picker.ShowDialog()==DialogResult.OK) field.Text=picker.SelectedPath; }); browse.Enabled=config==null;
                page.Controls.Add(Ui.Row(browse, Ui.Button("Validate location", (_,_) => {
                    try { root=RuntimePaths.Validate(field.Text,AppContext.BaseDirectory,SetupOperations.InstallRoot); var free=RuntimePaths.WriteTest(root); storageChecked=free>=512L*1024*1024;
                        status.Text=$"Write test passed · {free/1024.0/1024/1024:F1} GB available"+(storageChecked?"":" · At least 512 MB required"); }
                    catch(Exception ex) { storageChecked=false; status.Text=ex.Message; }
                },true)));
                page.Controls.Add(Ui.Text("Use a dedicated local NTFS folder such as D:\\M365CollectorData. Network shares, junctions, application folders and drive roots are rejected.")); break;
            case 4:
                page.Controls.Add(Ui.Text("DataRoot\n"+root,14,true));
                page.Controls.Add(Ui.Text("Config / Logs / Database / Cache / Service\nReports / Exports / Temp / Customers / <TenantId>\n\nNext creates the protected runtime structure, versioned configuration and SQLite database. Setup remains recoverable if a later step fails.")); break;
            case 5:
                page.Controls.Add(Ui.Text("M365CollectorService",18,true));
                page.Controls.Add(Ui.Text("Automatic startup · LocalService with a service-specific SID\n\nBinaries: "+SetupOperations.InstallRoot+"\nData: "+root+"\n\nNext installs and starts the service, then checks its heartbeat and version. This creates persistent Windows components."));
                page.Controls.Add(Ui.Button("Verify existing service",(_,_)=> { status.Text=SetupOperations.Healthy(new RuntimePaths(root))?"Service healthy. Next will continue setup.":"No healthy service detected yet."; })); break;
            case 6:
                page.Controls.Add(Ui.Text("Windows administrator authentication",18,true));
                page.Controls.Add(Ui.Text("The elevated Windows Administrators group is the application administrator mechanism in v0.1.0. Windows validates the account; M365Collector stores no local passwords.\n\nRuntime folders are restricted to Administrators, SYSTEM and the collection service. Operator and Read Only roles are defined for future access providers and cannot sign in yet.\n\nInitial administrator SID:\n"+WindowsSecurity.CurrentSid)); break;
            case 7:
                page.Controls.Add(Ui.Text("Verify the foundation before onboarding",18,true));
                page.Controls.Add(Ui.Text("Finish verifies configuration, database schema, storage access and a fresh service heartbeat with version 0.1.0. Only then is setup marked complete.\n\nThe next page is Customers → Add Customer.")); break;
        }
        page.Controls.Add(status);
    }
    private async Task Check(FlowLayoutPanel page)
    {
        SetBusy(true); status.Text="Checking dependencies…";
        try
        {
            var results=await SetupOperations.CheckDependencies(lifetime.Token); dependencyResults=results; dependenciesPassed=results.All(r=>!r.Required||r.Present);
            status.Text=string.Join("\n\n",results.Select(r=>$"{(r.Present?"PRESENT":"MISSING")} · {(r.Required?"Required":"Optional")} · {r.Name}\n{r.Detail}"));
        }
        catch { status.Text="Dependency checks did not complete. Retry."; }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value) { busy=value; next.Enabled=!value; back.Enabled=!value&&step>0&&config==null; cancel.Enabled=!value; }
    private async Task Advance()
    {
        SetBusy(true);
        try
        {
            if(step==2&&!dependenciesPassed) throw new InvalidOperationException("Run checks and resolve missing required dependencies first.");
            if(step==3&&!storageChecked) throw new InvalidOperationException("Validate the selected storage location first.");
            if(step==4)
            {
                config=SetupOperations.CreateRuntime(root);
                var log=new StructuredLog(new RuntimePaths(root),"gui");
                for(var index=0;index<dependencyResults.Count;index++)log.Write("DependencyCheck",dependencyResults[index].Present?"Present":"Missing",code:"Check"+index);
            }
            if(step==5 && !SetupOperations.Healthy(new RuntimePaths(root)))
                await SetupOperations.InstallAndStart(config!,new Progress<string>(message=>status.Text=message),lifetime.Token);
            if(step==6) { new CollectorDatabase(new RuntimePaths(root)).InitializeAdministrator(WindowsSecurity.CurrentSid); new StructuredLog(new RuntimePaths(root),"gui").Write("AdministratorInitialized","Success"); }
            if(step==7)
            {
                var paths=new RuntimePaths(root); config=new ConfigurationStore(paths).Load();
                if(!SetupOperations.Healthy(paths)||new CollectorDatabase(paths).SchemaVersion()!=ProductInfo.DatabaseSchema) throw new InvalidOperationException("Service health or database verification failed. Setup remains incomplete.");
                RuntimePaths.WriteTest(root); config=config with { SetupComplete=true }; new ConfigurationStore(paths).Save(config); InstallationRegistry.Register(config);
                new StructuredLog(paths,"gui").Write("SetupCompleted","Success"); CompletedConfig=config;
                SetBusy(false); DialogResult=DialogResult.OK; Close(); return;
            }
            if(config!=null)new StructuredLog(new RuntimePaths(root),"gui").Write("WizardStage","Success",code:"Step"+(step+1));
            step++; Render();
        }
        catch(Exception ex) { status.Text=ex.Message; status.ForeColor=Color.Firebrick; }
        finally { SetBusy(false); }
    }
    protected override void Dispose(bool disposing) { if(disposing) lifetime.Dispose(); base.Dispose(disposing); }
}
