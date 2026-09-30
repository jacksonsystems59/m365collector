using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Security;
namespace M365Collector.GUI;
internal sealed partial class MainForm
{
    private Guid? selectedTenant;
    private sealed record CustomerChoice(Customer Value) { public override string ToString()=>Value.Name; }
    private static Color HealthColor(CollectorHealth health)=>health switch
    {
        CollectorHealth.Healthy=>Color.FromArgb(24,125,75),CollectorHealth.Collecting=>Color.RoyalBlue,
        CollectorHealth.Failed=>Color.Firebrick,CollectorHealth.Attention or CollectorHealth.NotConfigured=>Color.DarkGoldenrod,
        _=>Color.SlateGray
    };
    private static CollectorHealth Overall(IEnumerable<ModuleState> modules)
    {
        var enabled=modules.Where(m=>m.Enabled).ToArray();if(enabled.Length==0)return CollectorHealth.Disabled;
        if(enabled.Any(m=>m.Health==CollectorHealth.Collecting))return CollectorHealth.Collecting;
        if(enabled.Any(m=>m.Health==CollectorHealth.Failed))return CollectorHealth.Failed;
        if(enabled.Any(m=>m.Health is CollectorHealth.Attention or CollectorHealth.NotConfigured))return CollectorHealth.Attention;
        return enabled.All(m=>m.Health==CollectorHealth.Healthy)?CollectorHealth.Healthy:CollectorHealth.Ready;
    }
    private static string When(DateTimeOffset? time)=>time?.ToLocalTime().ToString("g")??"Never";
    private static FlowLayoutPanel Card(FlowLayoutPanel parent,string title)
    {
        var card=new FlowLayoutPanel{Width=850,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(700,0),MaximumSize=new Size(1000,0),FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(18),Margin=new Padding(0,0,0,16),BackColor=Color.FromArgb(243,247,251),BorderStyle=BorderStyle.FixedSingle};
        var header=Ui.Text(title);header.Font=new Font("Segoe UI",14,FontStyle.Bold);card.Controls.Add(header);parent.Controls.Add(card);return card;
    }
    private ComboBox CustomerPicker(FlowLayoutPanel page,Action<Customer> changed,Guid? requested=null)
    {
        page.Controls.Add(Ui.Text("Customer"));var picker=new ComboBox{Width=500,DropDownStyle=ComboBoxStyle.DropDownList};
        var customers=store.GetCustomers();foreach(var customer in customers)picker.Items.Add(new CustomerChoice(customer));page.Controls.Add(picker);
        picker.SelectedIndexChanged+=(_,_)=>{if(picker.SelectedItem is CustomerChoice choice){selectedTenant=choice.Value.TenantId;changed(choice.Value);}};
        if(customers.Count>0){var id=requested??selectedTenant;var index=customers.ToList().FindIndex(c=>c.TenantId==id);picker.SelectedIndex=Math.Max(0,index);}else page.Controls.Add(Ui.Text("Add a customer to begin collecting and exploring audit records."));return picker;
    }
    private void Dashboard()
    {
        var page=Page("Dashboard");var customers=store.GetCustomers();var states=customers.SelectMany(c=>store.Modules(c.TenantId)).ToArray();var overall=Overall(states);
        var summary=Card(page,"Collection overview");var health=Ui.Text("● "+overall);health.ForeColor=HealthColor(overall);summary.Controls.Add(health);
        long today=0;foreach(var customer in customers)today+=store.Count(new(customer.TenantId,new DateTimeOffset(DateTime.UtcNow.Date,TimeSpan.Zero),DateTimeOffset.UtcNow.AddSeconds(1)));
        summary.Controls.Add(Ui.Text($"Customers: {customers.Count}    •    Records today (UTC): {today:N0}\nLast successful collection: {When(states.Select(s=>s.LastSuccess).Max())}\nModules requiring attention: {states.Count(s=>s.Enabled&&s.Health is CollectorHealth.Attention or CollectorHealth.NotConfigured or CollectorHealth.Failed)}"));
        try{var beat=JsonFile.Read<Heartbeat>(paths.Heartbeat);summary.Controls.Add(Ui.Text($"Service: {(beat.Time>DateTimeOffset.UtcNow.AddSeconds(-30)?"Recent heartbeat":"Attention — heartbeat is stale")} • {beat.Version} • {When(beat.Time)}"));}catch(Exception e)when(e is IOException or System.Text.Json.JsonException){summary.Controls.Add(Ui.Text("Service: Attention — heartbeat unavailable."));}
        page.Controls.Add(Ui.Text("Recent collection activity",heading:true));
        foreach(var run in customers.SelectMany(c=>store.Runs(c.TenantId,10)).OrderByDescending(r=>r.Started).Take(20))page.Controls.Add(Ui.Text($"{customers.Single(c=>c.TenantId==run.TenantId).Name} • {CollectorCatalog.Get(run.ModuleId).Name}\n{When(run.Started)} → {When(run.Ended)} • {run.State} • {run.Added:N0} records added"));
        var last=Path.Combine(paths.Root,"Config","last-update.json");if(File.Exists(last))page.Controls.Add(Ui.Text("Last update transaction:\n"+File.ReadAllText(last)));
        page.Controls.Add(Ui.Button("Refresh",Dashboard));
    }
    private void Customers()
    {
        var page=Page("All Customers");var status=Ui.Text("");
        foreach(var customer in store.GetCustomers())
        {
            var states=store.Modules(customer.TenantId);var overall=Overall(states);var card=Card(page,customer.Name);var health=Ui.Text("● "+overall);health.ForeColor=HealthColor(overall);card.Controls.Add(health);
            string certificate;
            try{using var cert=Certificates.Find(customer.Thumbprint);certificate=$"{(cert.NotAfter.ToUniversalTime()<DateTime.UtcNow.AddDays(30)?"Attention — expires soon":"Healthy")} • expires {cert.NotAfter:d}";}catch(Exception){certificate="Attention — certificate unavailable, expired or inaccessible";}
            card.Controls.Add(Ui.Text($"{customer.Identity?.DisplayName}\n{customer.Identity?.Domains.FirstOrDefault(d=>d.IsDefault)?.Name??customer.Identity?.Domains.FirstOrDefault(d=>d.IsInitial)?.Name}\nTenant: {customer.TenantId}\nCertificate: {certificate}\nModules: {states.Count(s=>s.Enabled)} enabled\nLast successful collection: {When(states.Select(s=>s.LastSuccess).Max())}\nLast problem: {states.Where(s=>s.Error!=null).OrderByDescending(s=>s.LastFailure).FirstOrDefault()?.Error??"None recorded"}"));
            var buttons=new FlowLayoutPanel{AutoSize=true};buttons.Controls.Add(Ui.Button("Open Customer",()=>Modules(customer.TenantId)));
            if(Authorization.Allows(user,Capability.Collect))buttons.Controls.Add(Ui.AsyncButton("Collect Now",()=>{Authorization.Require(user,Capability.Collect);foreach(var module in states.Where(s=>s.Enabled))store.RequestRun(customer.TenantId,module.ModuleId);status.Text="Enabled modules queued for the Windows Service.";return Task.CompletedTask;},status));
            card.Controls.Add(buttons);
        }
        page.Controls.Add(Ui.Button("Refresh",Customers));page.Controls.Add(status);
    }
    private void Modules()=>Modules(null);
    private void Modules(Guid? tenant)
    {
        var page=Page("Collection Modules");var status=Ui.Text("");var summary=Ui.Text("");var cards=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};
        Customer? selected=null;var labels=new Dictionary<string,Label>();
        void RefreshState()
        {
            if(selected==null)return;var states=store.Modules(selected.TenantId);var overall=Overall(states);summary.ForeColor=HealthColor(overall);
            summary.Text=$"● Platform status: {overall}\nEnabled: {states.Count(s=>s.Enabled)}    Healthy: {states.Count(s=>s.Enabled&&s.Health==CollectorHealth.Healthy)}    Attention: {states.Count(s=>s.Enabled&&s.Health is CollectorHealth.Attention or CollectorHealth.NotConfigured)}    Failed: {states.Count(s=>s.Enabled&&s.Health==CollectorHealth.Failed)}\nLast collection cycle: {When(states.Select(s=>s.LastAttempt).Max())}";
            foreach(var state in states)if(labels.TryGetValue(state.ModuleId,out var label)){label.ForeColor=HealthColor(state.Health);label.Text=ModuleText(state);}
        }
        void Populate(Customer customer)
        {
            selected=customer;foreach(Control old in cards.Controls.Cast<Control>().ToArray())old.Dispose();cards.Controls.Clear();labels.Clear();
            foreach(var definition in CollectorCatalog.All)
            {
                var state=store.Modules(customer.TenantId).Single(m=>m.ModuleId==definition.Id);var card=Card(cards,definition.Name);card.Controls.Add(Ui.Text(definition.Description));var health=Ui.Text("");card.Controls.Add(health);labels[definition.Id]=health;
                var buttons=new FlowLayoutPanel{AutoSize=true,MaximumSize=new Size(790,0)};
                if(Authorization.Allows(user,Capability.Collect))
                {
                    buttons.Controls.Add(Ui.AsyncButton(state.Enabled?"Disable":"Enable",()=>{Authorization.Require(user,Capability.Collect);var current=store.Modules(customer.TenantId).Single(m=>m.ModuleId==definition.Id);store.Configure(customer.TenantId,definition.Id,!current.Enabled,current.ScheduleMinutes,current.RetentionDays,current.RetentionEnabled);Populate(customer);status.Text="Configuration saved. Enabling queues a service-side permission check.";return Task.CompletedTask;},status));
                    buttons.Controls.Add(Ui.AsyncButton("Run Now",()=>{Authorization.Require(user,Capability.Collect);store.RequestRun(customer.TenantId,definition.Id);status.Text="Queued if enabled; an already-running module will not overlap.";return Task.CompletedTask;},status));
                    buttons.Controls.Add(Ui.Button("Configure",()=>ConfigureCard(card,customer,definition,status)));
                }
                buttons.Controls.Add(Ui.Button("View Data",()=>{if(definition.Id=="tenant-identity")ShowIdentity(customer);else Explorer(customer.TenantId,definition.Id);}));
                card.Controls.Add(buttons);
                if(Authorization.Allows(user,Capability.ManageCustomers))card.Controls.Add(Ui.Button("Update Tenant Permissions",()=>PermissionPanel(card,customer,definition,status)));
                if(definition.Id=="sign-ins")
                {
                    var shortcuts=new FlowLayoutPanel{AutoSize=true};shortcuts.Controls.Add(Ui.Button("Failed Sign-ins",()=>Explorer(customer.TenantId,"sign-ins","Failed")));shortcuts.Controls.Add(Ui.Button("Location / IP",()=>Explorer(customer.TenantId,"sign-ins",group:"IP / location")));card.Controls.Add(shortcuts);
                }
            }
            Card(cards,"Exchange Message Trace").Controls.Add(Ui.Text("Coming later — no ExchangeOnlineManagement dependency is installed."));RefreshState();
        }
        CustomerPicker(page,Populate,tenant);page.Controls.Add(summary);
        if(Authorization.Allows(user,Capability.Collect))page.Controls.Add(Ui.AsyncButton("Run All Enabled Modules",()=>{Authorization.Require(user,Capability.Collect);if(selected!=null)foreach(var state in store.Modules(selected.TenantId).Where(s=>s.Enabled))store.RequestRun(selected.TenantId,state.ModuleId);status.Text="Enabled modules queued for background collection.";return Task.CompletedTask;},status));
        page.Controls.Add(status);page.Controls.Add(cards);
        var refresh=new System.Windows.Forms.Timer{Interval=5000};refresh.Tick+=(_,_)=>{try{RefreshState();}catch(Exception e){status.Text="Status refresh failed: "+e.GetType().Name;}};page.Disposed+=(_,_)=>refresh.Dispose();refresh.Start();
    }
    private static string ModuleText(ModuleState state)
    {
        var definition=CollectorCatalog.Get(state.ModuleId);
        return $"● {state.Health}     {(state.Enabled?"ENABLED":"DISABLED")}\nLast successful: {When(state.LastSuccess)}\nLast attempted: {When(state.LastAttempt)}    Last failure: {When(state.LastFailure)}\nStored records: {(state.ModuleId=="tenant-identity"?"Tenant metadata":state.TotalRecords.ToString("N0"))}    Added by last run: {state.LastAdded:N0}\nSchedule: every {state.ScheduleMinutes} minutes\nPermission: {definition.Permission} — {state.PermissionStatus}\nDependencies: {state.DependencyStatus}\nRetention: {state.RetentionDays} days ({(state.RetentionEnabled?"cleanup enabled":"cleanup off")})\nLast error: {state.Error??"None"}";
    }
    private void ShowIdentity(Customer customer)
    {
        var page=Page("Tenant Identity — "+customer.Name);page.Controls.Add(Ui.Text($"Tenant: {customer.TenantId}\nName: {customer.Identity?.DisplayName}\nCollected: {When(customer.Identity?.CollectedAt)}\n"+string.Join("\n",customer.Identity?.Domains.Select(d=>d.Name+(d.IsDefault?" • default":"")+(d.IsInitial?" • initial":""))??[])));page.Controls.Add(Ui.Button("Back to modules",()=>Modules(customer.TenantId)));
    }
    private void ConfigureCard(FlowLayoutPanel card,Customer customer,CollectorDefinition module,Label status)
    {
        if(card.Controls.OfType<FlowLayoutPanel>().Any(p=>p.Name=="Configuration"))return;
        var state=store.Modules(customer.TenantId).Single(m=>m.ModuleId==module.Id);var form=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Name="Configuration"};
        form.Controls.Add(Ui.Text("Schedule (minutes)"));var interval=new ComboBox{Width=200,DropDownStyle=ComboBoxStyle.DropDownList,DataSource=CollectorCatalog.Intervals.ToArray()};interval.SelectedItem=state.ScheduleMinutes;form.Controls.Add(interval);
        form.Controls.Add(Ui.Text("Retention days"));var days=new NumericUpDown{Minimum=1,Maximum=3650,Value=state.RetentionDays,Width=200,Enabled=user.Role==LocalRole.Administrator};form.Controls.Add(days);
        var purge=new CheckBox{AutoSize=true,Text="Allow background deletion of this module's events older than the retention period",Checked=state.RetentionEnabled,Enabled=user.Role==LocalRole.Administrator};form.Controls.Add(purge);
        form.Controls.Add(Ui.AsyncButton("Save configuration",()=>{Authorization.Require(user,Capability.Collect);var current=store.Modules(customer.TenantId).Single(m=>m.ModuleId==module.Id);if((purge.Checked!=current.RetentionEnabled||(int)days.Value!=current.RetentionDays))Authorization.Require(user,Capability.ManageCustomers);store.Configure(customer.TenantId,module.Id,current.Enabled,(int)interval.SelectedItem!,(int)days.Value,purge.Checked);form.Dispose();status.Text="Schedule and retention policy saved.";return Task.CompletedTask;},status));card.Controls.Add(form);
    }
    private void PermissionPanel(FlowLayoutPanel card,Customer customer,CollectorDefinition module,Label status)
    {
        if(card.Controls.OfType<FlowLayoutPanel>().Any(p=>p.Name=="Permissions"))return;
        var panel=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Name="Permissions"};
        panel.Controls.Add(Ui.Text($"Review additional Microsoft application permission\nTenant: {customer.TenantId}\nExisting application: {customer.ClientId}\nAPI: {module.Resource}\nPermission: {module.Permission}\nWhy required? {module.Reason}\nOnly this permission is added. Existing permissions and certificate are preserved."));
        foreach(var browser in new[]{false,true})panel.Controls.Add(Ui.AsyncButton(browser?"Review & consent with browser":"Review & consent with Microsoft",async()=>
        {
            Authorization.Require(user,Capability.ManageCustomers);if(!Guid.TryParse(store.Setting("BootstrapClientId"),out var id))throw new InvalidOperationException("Configure the public-client ID in Administration, or add the displayed application permission in Entra and grant administrator consent manually.");
            using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(60)};await using(var session=await MicrosoftBootstrap.SignInAsync(id,"",Handle,browser,client,CancellationToken.None))await session.GrantCollectorPermissionAsync(customer,module,new Progress<string>(text=>status.Text=text),CancellationToken.None);
            store.RequestRun(customer.TenantId,module.Id);status.Text="Temporary administrator session ended. If enabled, service verification is queued; otherwise enable the module. Consent may need time to propagate.";
        },status));
        panel.Controls.Add(Ui.Button("Open Entra for manual consent",()=>Ui.Open("https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade")));panel.Controls.Add(Ui.Button("Copy permission",()=>Clipboard.SetText(module.Permission)));card.Controls.Add(panel);
    }
}
