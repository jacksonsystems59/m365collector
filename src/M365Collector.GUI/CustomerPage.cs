using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Security;
using M365Collector.Storage;

namespace M365Collector.GUI;

public sealed class CustomerPage : UserControl
{
    private readonly RuntimePaths paths;
    private readonly CollectorDatabase db;
    private readonly TextBox name, tenant, domain, client, thumbprint, setupClient;
    private readonly Label status=Ui.Text("");
    private readonly CheckBox review;
    private readonly List<Button> actions=[];
    private readonly CancellationTokenSource lifetime=new();
    private readonly Action<Customer> connected;
    private bool busy;
    public CustomerPage(RuntimePaths paths, Customer? customer, Action<Customer> connected)
    {
        this.paths=paths; this.connected=connected; db=new(paths); Dock=DockStyle.Fill;
        var page=Ui.Page(customer==null?"Add Customer":"Customer · "+customer.DisplayName,"Certificate-based unattended authentication · No administrator passwords are entered here"); Controls.Add(page);
        name=Ui.Field(page,"Customer display name",customer?.DisplayName??"");
        tenant=Ui.Field(page,"Tenant ID (GUID)",customer?.TenantId.ToString("D")??"",customer!=null);
        domain=Ui.Field(page,"Default domain — discovered by Test Connection",customer?.DefaultDomain??"",true);
        client=Ui.Field(page,"Application / Client ID",customer?.ClientId==Guid.Empty?"":customer?.ClientId.ToString("D")??"");
        thumbprint=Ui.Field(page,"Certificate — LocalMachine / My / thumbprint",customer?.Certificate?.Thumbprint??"");
        page.Controls.Add(Ui.Row(Action("Create certificate",CreateCertificate),Action("Export public .cer",ExportCertificate),Action("Prepare key access",PrepareCertificate)));
        page.Controls.Add(Ui.Text("A separate non-exportable RSA machine certificate is used for each tenant. New certificates expire in one year. Only the public .cer is exported under DataRoot. Prepare key access restricts an existing certificate's private key to administrators, SYSTEM and this service."));
        var tabs=new TabControl { Width=850, Height=410, Margin=new Padding(0,0,0,20) };
        var guidedTab=new TabPage("Guided Entra Setup · preferred"); var manualTab=new TabPage("Manual Entra Setup"); tabs.TabPages.AddRange([guidedTab,manualTab]); page.Controls.Add(tabs);
        var guided=Ui.Page("Guided setup", "Sign in and consent only on Microsoft-hosted pages."); guided.Padding=new Padding(15); guidedTab.Controls.Add(guided);
        guided.Controls.Add(Ui.Text("Supply your organisation's approved setup public-client app ID. In Entra, register a setup app with a Mobile and desktop platform and http://localhost redirect URI. Configure delegated Microsoft Graph Application.ReadWrite.All and obtain administrator consent. For cross-tenant use, the approved setup app must support those tenants. This is separate from each customer's runtime app.",10));
        setupClient=Ui.Field(guided,"Approved setup public-client application ID");
        review=Ui.Check("I approve temporary Application.ReadWrite.All delegated setup access to create the dedicated app and service principal."); guided.Controls.Add(review);
        guided.Controls.Add(Action("Start Microsoft browser setup",StartGuided,true));
        guided.Controls.Add(Ui.Text("Runtime permission shown below is configured, never silently consented. After setup, use Review admin consent to grant it in Entra. Temporary setup tokens are discarded after each attempt. Cancel browser setup using the button below or close this page.",10));
        var manual=Ui.Page("Manual setup", "Use when policy does not allow automated registration."); manual.Padding=new Padding(15); manualTab.Controls.Add(manual);
        manual.Controls.Add(Ui.Text("1. Open Microsoft Entra admin center and switch to the customer tenant.\n2. Open App registrations → New registration.\n3. Name the app SterlingTech M365Collector; choose single tenant.\n4. Record Directory (tenant) ID and Application (client) ID above.\n5. Create a certificate here (or select a non-exportable RSA CNG machine certificate and Prepare key access).\n6. Export public .cer. In the app, open Certificates & secrets → Certificates → Upload certificate.\n7. Open API permissions → Add a permission → Microsoft Graph → Application permissions.\n8. Add only the runtime permission listed below. Remove unrelated default delegated permissions.\n9. Have an authorised administrator review and grant tenant-wide admin consent.\n10. Return here, confirm IDs and certificate thumbprint, then Test Connection.\n11. A successful service-side test verifies tenant identity and activates the customer.",10));
        page.Controls.Add(Ui.Text("Required Microsoft application permissions",15,true));
        var enabledIdentity=customer==null||db.ModuleEnabled(customer.TenantId,"tenant-identity");
        foreach(var permission in ModuleCatalog.Permissions(enabledIdentity?["tenant-identity"]:[]))
            page.Controls.Add(Ui.Text(permission.Name+"  ·  Tenant Identity\n"+permission.Reason));
        if(!enabledIdentity)page.Controls.Add(Ui.Text("No modules enabled. Enable Tenant Identity in Collection Modules before guided setup or connection validation."));
        page.Controls.Add(Ui.Row(Action("Open Entra admin center",()=> { Ui.OpenUrl("https://entra.microsoft.com"); return Task.CompletedTask; }),Action("Review admin consent",()=> {
            var value=Read(false); if(value.ClientId==Guid.Empty)throw new ArgumentException("Enter a client ID first.");
            Ui.OpenUrl($"https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{value.ClientId:D}/isMSAApp~/false");
            status.Text="Switch to customer tenant "+value.TenantId+" and review Organization.Read.All (Application) before granting consent. Then return and Test Connection."; return Task.CompletedTask;
        })));
        page.Controls.Add(Ui.Row(Action("Copy Tenant ID",()=>Copy(tenant.Text)),Action("Copy Client ID",()=>Copy(client.Text)),Action("Copy permission",()=>Copy("Organization.Read.All"))));
        page.Controls.Add(Ui.Text("Saving edits returns the customer to Draft until validated. Test Connection saves the onboarding configuration as Draft and asks the service to validate it. This also proves the service can access the certificate without an interactive administrator session."));
        page.Controls.Add(Ui.Row(Action("Save as Draft",SaveDraft),Action("Test Connection",TestConnection,true),Ui.Button("Cancel browser/test wait",(_,_)=>lifetime.Cancel())));
        page.Controls.Add(status);
    }
    private Button Action(string text,Func<Task> work,bool primary=false)
    {
        var button=Ui.Button(text,async (_,_)=>
        {
            if(busy)return; busy=true; actions.ForEach(b=>b.Enabled=false); status.ForeColor=Ui.Teal;
            try { await work(); }
            catch(OperationCanceledException) { status.Text="Cancelled. An already-queued service job continues independently; reopen the customer to see its state."; }
            catch(Exception ex)
            {
                status.ForeColor=Color.Firebrick;
                status.Text=ex is ArgumentException or InvalidOperationException or NotSupportedException ? ex.Message : AuthenticationErrors.Explain(AuthenticationErrors.Code(ex));
                new StructuredLog(paths,"gui").Write("CustomerOperation","Failure",code:AuthenticationErrors.Code(ex));
            }
            finally { busy=false; if(!IsDisposed) { actions.ForEach(b=>b.Enabled=true); ((FlowLayoutPanel)Controls[0]).ScrollControlIntoView(status); } }
        },primary); actions.Add(button); return button;
    }
    private static Task Copy(string text) { if(!string.IsNullOrWhiteSpace(text))Clipboard.SetText(text); return Task.CompletedTask; }
    private Customer Read(bool requireAuthentication)
    {
        if(!Guid.TryParse(tenant.Text.Trim(),out var tenantId)||tenantId==Guid.Empty)throw new ArgumentException("Enter a valid tenant ID.");
        if(string.IsNullOrWhiteSpace(name.Text))throw new ArgumentException("Enter the customer display name.");
        Guid clientId=Guid.Empty;
        if(!string.IsNullOrWhiteSpace(client.Text)&&!Guid.TryParse(client.Text.Trim(),out clientId))throw new ArgumentException("Enter a valid application/client ID.");
        var normalized=thumbprint.Text.Replace(" ","").Trim().ToUpperInvariant();
        CertificateReference? reference=normalized.Length==0?null:new(normalized);
        if(reference!=null)CollectorDatabase.ValidateCertificateReference(reference);
        if(requireAuthentication&&(clientId==Guid.Empty||reference==null))throw new ArgumentException("Client ID and machine certificate thumbprint are required.");
        return new(tenantId,name.Text.Trim(),domain.Text,clientId,reference,CustomerState.Draft);
    }
    private Task CreateCertificate()
    {
        var value=Read(false);
        if(value.Certificate!=null)throw new InvalidOperationException("A certificate is already selected. Clear the reference only when intentionally creating a replacement.");
        var reference=new CertificateStore().Create(value.TenantId); thumbprint.Text=reference.Thumbprint;
        status.Text="Machine certificate created with a non-exportable private key and service ACL. Save as Draft to retain its reference. Export its public .cer for Entra.";
        new StructuredLog(paths,"gui").Write("CertificateCreated","Success",value.TenantId); return Task.CompletedTask;
    }
    private Task ExportCertificate()
    {
        var value=Read(false); if(value.Certificate==null)throw new ArgumentException("Select or create a certificate first.");
        paths.CreateCustomer(value.TenantId); var destination=Path.Combine(paths.CustomerRoot(value.TenantId),"Exports", "M365Collector-"+value.Certificate.Thumbprint+".cer");
        File.WriteAllBytes(destination,new CertificateStore().PublicCertificate(value.Certificate)); status.Text="Public certificate exported: "+destination; return Task.CompletedTask;
    }
    private Task PrepareCertificate()
    {
        var value=Read(false); if(value.Certificate==null)throw new ArgumentException("Enter a certificate thumbprint first.");
        new CertificateStore().PrepareForService(value.Certificate); status.Text="Private-key access prepared for M365CollectorService."; return Task.CompletedTask;
    }
    private Task SaveDraft()
    {
        var value=Read(false); db.SaveDraft(value); new StructuredLog(paths,"gui").Write("CustomerSavedDraft","Success",value.TenantId); status.Text="Draft saved. The customer becomes Active only after a successful service-side Test Connection."; return Task.CompletedTask;
    }
    private async Task StartGuided()
    {
        if(!review.Checked)throw new InvalidOperationException("Review and approve the temporary delegated setup permission first.");
        if(!Guid.TryParse(setupClient.Text,out var setupId))throw new ArgumentException("Supply an approved setup public-client application ID.");
        var value=Read(false); if(value.Certificate==null)throw new ArgumentException("Create or select a customer certificate first.");
        var checkpoint=Path.Combine(paths.CustomerRoot(value.TenantId),"Cache","guided-setup.json");
        if(value.ClientId!=Guid.Empty&&!File.Exists(checkpoint))throw new InvalidOperationException("A client ID is already entered. Use Manual Entra Setup for that application, or clear the ID to create a dedicated new one.");
        db.SaveDraft(value);
        if(!db.ModuleEnabled(value.TenantId,"tenant-identity"))throw new InvalidOperationException("Enable Tenant Identity in Collection Modules before requesting its setup permission.");
        new CertificateStore().PrepareForService(value.Certificate);
        using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(45) };
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var result=await new GuidedSetup(http,new CertificateStore()).ConfigureAsync(setupId,value,ModuleCatalog.Permissions(["tenant-identity"]),checkpoint,new Progress<string>(message=> { if(!IsDisposed)status.Text=message; }),timeout.Token);
            client.Text=result.ClientId.ToString("D"); db.SaveDraft(Read(true));
            status.Text="Administrator Setup Session Ended. App created: "+result.ClientId+". Next: Review admin consent in Entra, then Test Connection.";
            new StructuredLog(paths,"gui").Write("GuidedSetup","Success",value.TenantId);
        }
        finally { if(!IsDisposed)status.Text+="\nAdministrator Setup Session Ended — temporary setup tokens discarded."; }
    }
    private async Task TestConnection()
    {
        if(!SetupOperations.Healthy(paths))throw new InvalidOperationException("M365CollectorService must be healthy before testing.");
        var value=Read(true); db.SaveDraft(value);
        new CertificateStore().PrepareForService(value.Certificate!);
        if(!db.ModuleEnabled(value.TenantId,"tenant-identity"))throw new InvalidOperationException("Enable Tenant Identity in Collection Modules before testing.");
        var id=db.Enqueue(value.TenantId,"Validate"); status.Text="Testing certificate authentication and tenant identity in the Windows Service…";
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(110));
        while(true)
        {
            await Task.Delay(1000,timeout.Token); var job=db.GetJob(id)!;
            if(job.Status=="Failed") { status.Text="Connection failed. "+AuthenticationErrors.Explain(job.ResultCode??"Unknown"); return; }
            if(job.Status!="Succeeded")continue;
            domain.Text=job.Identity!.DefaultDomain;
            status.Text=$"Connection Successful\nCustomer: {value.DisplayName}\nTenant: {job.Identity.DisplayName} ({value.TenantId:D})\nDefault domain: {domain.Text}\nAuthentication: Certificate\n\n✓ Service application authentication successful\n✓ Tenant verified\n✓ Required API access verified\n✓ Tenant identity retrieved\nCustomer saved as Active.";
            new StructuredLog(paths,"gui").Write("CustomerActivated","Success",value.TenantId);
            var continueButton=Ui.Button("Continue to Collection Modules",(_,_)=>connected(db.Find(value.TenantId)!),true);
            ((FlowLayoutPanel)Controls[0]).Controls.Add(continueButton); ((FlowLayoutPanel)Controls[0]).ScrollControlIntoView(status); return;
        }
    }
    protected override void Dispose(bool disposing) { if(disposing) { lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}
