using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Storage;

namespace M365Collector.GUI;

public sealed class MainForm : Form
{
    private readonly RuntimeConfig config;
    private readonly RuntimePaths paths;
    private readonly CollectorDatabase db;
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly Label health = new() { Dock=DockStyle.Bottom, Height=36, Padding=new Padding(22,8,0,0), BackColor=Color.White, ForeColor=Ui.Muted };
    private readonly System.Windows.Forms.Timer timer = new() { Interval=10000 };
    private Customer? selectedCustomer;
    public MainForm(RuntimeConfig config, bool addFirst = false)
    {
        this.config=config; paths=new(config.DataRoot); db=new(paths);
        Text="M365Collector " + ProductInfo.Version; Width=1260; Height=900; MinimumSize=new Size(1060,720);
        StartPosition=FormStartPosition.CenterScreen; Font=new Font("Segoe UI",10); BackColor=Ui.Canvas;
        var sidebar=new FlowLayoutPanel { Dock=DockStyle.Left, Width=225, FlowDirection=FlowDirection.TopDown, WrapContents=false, Padding=new Padding(16,24,16,0), BackColor=Ui.Navy };
        var brand=Ui.Text("STERLINGTECH\nM365COLLECTOR",13,true); brand.ForeColor=Color.White; sidebar.Controls.Add(brand);
        var version=Ui.Text("Foundation release  •  v"+ProductInfo.Version,9); version.ForeColor=Color.LightSteelBlue; sidebar.Controls.Add(version);
        foreach(var name in new[]{"Dashboard","Customers","Add Customer","Collection Modules","Audit Explorer","Reports","Administration","Settings","Logs"})
        {
            var button=Ui.Button(name,(_,_)=>Navigate(name)); button.Width=190; button.Height=43; button.BackColor=Ui.Navy; button.ForeColor=Color.White; button.FlatAppearance.BorderSize=0; button.TextAlign=ContentAlignment.MiddleLeft; sidebar.Controls.Add(button);
        }
        Controls.Add(content); Controls.Add(sidebar); Controls.Add(health);
        timer.Tick+=(_,_)=>RefreshHealth(); timer.Start(); RefreshHealth();
        FormClosing+=(_,_)=>timer.Stop(); Navigate(addFirst?"Add Customer":"Dashboard");
    }
    private void RefreshHealth() => health.Text=$"M365Collector {ProductInfo.Version}    |    Service: {(SetupOperations.Healthy(paths)?"Healthy · "+ProductInfo.Version:"Unavailable, stale or version mismatch")}    |    Windows administrator";
    private void ShowPage(Control page)
    {
        foreach(Control old in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(old); old.Dispose(); }
        content.Controls.Add(page);
    }
    public void Navigate(string name)
    {
        RefreshHealth();
        if(name=="Add Customer") { ShowCustomer(null); return; }
        var page=Ui.Page(name, "M365Collector " + ProductInfo.Version + " · Customer collection platform");
        switch(name)
        {
            case "Dashboard":
                var customers=db.Customers();
                page.Controls.Add(Ui.Text($"{customers.Count(c=>c.State==CustomerState.Active)} active tenants     /     {customers.Count(c=>c.State==CustomerState.Draft)} drafts",22,true));
                page.Controls.Add(Ui.Text("Tenant Identity is ready\nCertificate-based collection runs independently in M365CollectorService. Collection interval: "+config.CollectionIntervalMinutes+" minutes."));
                page.Controls.Add(Ui.Row(Ui.Button("Add customer",(_,_)=>Navigate("Add Customer"),true),Ui.Button("View customers",(_,_)=>Navigate("Customers"))));
                page.Controls.Add(Ui.Text("Runtime storage",14,true)); page.Controls.Add(Ui.Text(paths.Root));
                page.Controls.Add(Ui.Text("Audit Explorer and reporting collectors are planned for future versions. Automatic updates are not enabled.")); break;
            case "Customers":
                page.Controls.Add(Ui.Button("Add customer",(_,_)=>ShowCustomer(null),true));
                if(db.Customers().Count==0) page.Controls.Add(Ui.Text("No customers yet. Add your first tenant using guided or manual Entra setup."));
                foreach(var customer in db.Customers())
                {
                    page.Controls.Add(Ui.Text(customer.DisplayName+"  ·  "+customer.State,15,true));
                    page.Controls.Add(Ui.Text($"{customer.TenantId:D}\n{customer.DefaultDomain}\nLast validation: {customer.ValidatedAt?.ToLocalTime().ToString("g") ?? "Not validated"}"));
                    page.Controls.Add(Ui.Row(Ui.Button("Open customer",(_,_)=>ShowCustomer(customer)),Ui.Button("Collection modules",(_,_)=> { selectedCustomer=customer; Navigate("Collection Modules"); })));
                }
                break;
            case "Collection Modules":
                var tenants=db.Customers();
                if(tenants.Count==0) { page.Controls.Add(Ui.Text("Add a customer to select its modules.")); break; }
                var picker=new ComboBox { Width=680, DropDownStyle=ComboBoxStyle.DropDownList, DisplayMember=nameof(Customer.DisplayName), DataSource=tenants.ToList(), Margin=new Padding(0,0,0,22) };
                picker.SelectedItem=tenants.FirstOrDefault(c=>c.TenantId==selectedCustomer?.TenantId)??tenants[0];
                page.Controls.Add(picker);
                var module=Ui.Check("Tenant Identity — verify tenant identity and verified domains", db.ModuleEnabled(((Customer)picker.SelectedItem!).TenantId,"tenant-identity"));
                picker.SelectedIndexChanged+=(_,_)=>module.Checked=db.ModuleEnabled(((Customer)picker.SelectedItem!).TenantId,"tenant-identity"); page.Controls.Add(module);
                page.Controls.Add(Ui.Text("Required application permission: Organization.Read.All\nReason: read tenant identity and verified domains. No PowerShell dependencies."));
                var moduleStatus=Ui.Text("");
                page.Controls.Add(Ui.Row(Ui.Button("Save module selection",(_,_)=> {
                    var customer=(Customer)picker.SelectedItem!; db.SetModule(customer.TenantId,"tenant-identity",module.Checked); new StructuredLog(paths,"gui").Write("ModuleConfiguration","Success",customer.TenantId); moduleStatus.Text="Module selection saved for "+customer.DisplayName;
                },true),Ui.Button("Collect now",(_,_)=> {
                    var customer=(Customer)picker.SelectedItem!;
                    if(customer.State!=CustomerState.Active) { moduleStatus.Text="Test Connection successfully before collecting."; return; }
                    if(!db.ModuleEnabled(customer.TenantId,"tenant-identity")) { moduleStatus.Text="Enable and save Tenant Identity first."; return; }
                    if(!SetupOperations.Healthy(paths)) { moduleStatus.Text="Service is not healthy. Start or repair the service first."; return; }
                    moduleStatus.Text="Collection queued in service. Job "+db.Enqueue(customer.TenantId,"Collect")+". See Logs and the customer's Data folder for results.";
                })));
                page.Controls.Add(moduleStatus);
                foreach(var manifest in ModuleCatalog.All.Where(m=>!m.Implemented)) { var check=Ui.Check(manifest.Name+" — Not yet implemented"); check.Enabled=false; page.Controls.Add(check); }
                break;
            case "Administration":
                page.Controls.Add(Ui.Text("Local application security",18,true));
                page.Controls.Add(Ui.Text("Authentication: elevated Windows administrator\nAuthorisation: M365Collector Administrator\nInitial administrator SID: "+config.AdministratorSid+"\n\nOperator and Read Only are reserved roles. Role assignment and additional authentication providers are coming in a future version.\n\nCertificate rotation: replace the customer's machine-store certificate reference, upload the new public certificate in Entra, and validate through the service before removing the old credential.")); break;
            case "Settings":
                page.Controls.Add(Ui.Text($"Application version: {ProductInfo.Version}\nInstalled version: {config.InstalledVersion}\nConfiguration schema: {config.SchemaVersion}\nDatabase schema: {db.SchemaVersion()}\n\nDataRoot: {paths.Root}\nBinaries: {config.InstallRoot}\nService: {ProductInfo.ServiceName}\n\nNo automatic update checks or downloads are performed.")); break;
            case "Logs":
                page.Controls.Add(Ui.Text("Structured operational events; timestamps are UTC. Customer IDs identify tenant-specific activity."));
                var logs=new TextBox { Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, WordWrap=false, Width=850, Height=520, Font=new Font("Consolas",9) };
                void Reload() => logs.Text=string.Join(Environment.NewLine, Directory.EnumerateFiles(Path.Combine(paths.Root,"Logs"),"*.jsonl").OrderDescending().Take(4).SelectMany(file=>File.ReadLines(file).TakeLast(150)));
                Reload(); page.Controls.Add(Ui.Button("Refresh logs",(_,_)=>Reload())); page.Controls.Add(logs); break;
            default:
                page.Controls.Add(Ui.Text("Coming in a future version",20,true));
                page.Controls.Add(Ui.Text(name=="Audit Explorer"?"A dedicated workspace for high-volume audit searches by customer, time, user, workload, operation, object, path, site, IP address, location and result. Audit data remains separate from reports.":"Reports and CSV/PDF export with customer, date range, filters and generation time are planned. No report engine is included in v0.1.0.")); break;
        }
        ShowPage(page);
    }
    private void ShowCustomer(Customer? customer) => ShowPage(new CustomerPage(paths,customer,tenant=> { selectedCustomer=tenant; Navigate("Collection Modules"); }));
    protected override void Dispose(bool disposing) { if(disposing)timer.Dispose(); base.Dispose(disposing); }
}
