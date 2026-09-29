using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Security;
using M365Collector.Storage;
using System.Security.Cryptography.X509Certificates;
namespace M365Collector.GUI;
internal sealed class CustomerPage : UserControl
{
    private readonly CollectorStore store; private readonly LocalUser user; private readonly RuntimePaths paths;
    private readonly FlowLayoutPanel panel = Ui.Stack(); private readonly Label status = Ui.Text("");
    private readonly TextBox name, hint; private TextBox? tenant, client, thumbprint;
    private X509Certificate2? certificate; private bool busy;
    public CustomerPage(CollectorStore store, LocalUser user, RuntimePaths paths)
    {
        this.store = store; this.user = user; this.paths = paths; Dock = DockStyle.Fill; Controls.Add(panel);
        panel.Controls.Add(Ui.Text("Add Customer", heading: true)); name = Ui.Field(panel, "Customer Name"); hint = Ui.Field(panel, "Known Domain / Tenant (optional)");
        panel.Controls.Add(Ui.Text("An authorised administrator signs in through Microsoft. M365Collector never receives or stores the Microsoft password."));
        panel.Controls.Add(Ui.AsyncButton("Connect Microsoft 365 Tenant", () => Connect(false), status));
        panel.Controls.Add(Ui.AsyncButton("Use Microsoft system browser", () => Connect(true), status));
        panel.Controls.Add(Ui.Button("Advanced / Manual Setup", Manual)); panel.Controls.Add(status);
    }
    private void Check()
    {
        Authorization.Require(user, Capability.ManageCustomers);
        if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Length > 150) throw new ArgumentException("Enter a customer name of 1–150 characters.");
    }
    private async Task Connect(bool browser)
    {
        if (busy) throw new InvalidOperationException("An onboarding operation is already running.");
        Check();
        if (!Guid.TryParse(store.Setting("BootstrapClientId"), out var bootstrap)) throw new InvalidOperationException("Configure your organisation's public-client bootstrap application in Administration, or choose Advanced / Manual Setup. See docs/ENTRA-ONBOARDING.md.");
        busy = true;
        try
        {
            var handle = FindForm()!.Handle;
            using var progressDialog = new Form(); Ui.Style(progressDialog, "Connect Microsoft 365 Tenant", 800, 620);
            var progressPanel = Ui.Stack(); progressDialog.Controls.Add(progressPanel); progressPanel.Controls.Add(Ui.Text("Connect Microsoft 365 Tenant", heading: true));
            progressPanel.Controls.Add(Ui.Text("Authentication stays within Microsoft. MFA, passkeys and Conditional Access remain in Microsoft's sign-in process."));
            var progressText = Ui.Text("Ready for Microsoft sign-in."); progressPanel.Controls.Add(progressText);
            var progress = new Progress<string>(text => progressText.Text += "\n" + text);
            var complete = false;
            progressDialog.FormClosing += (_, e) => { if (!complete) e.Cancel = true; };
            progressDialog.Shown += async (_, _) =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    var session = await MicrosoftBootstrap.SignInAsync(bootstrap, hint.Text, progressDialog.Handle, browser, http, CancellationToken.None);
                    progressText.Text = "✓ Microsoft sign-in complete";
                    var customer = await OnboardingWorkflow.ProvisionAsync(session, name.Text.Trim(), id =>
                    {
                        certificate?.Dispose(); certificate = Certificates.Create(id);
                        Manual(); tenant!.Text = id.ToString(); thumbprint!.Text = certificate.Thumbprint;
                        progressText.Text += $"\n✓ Certificate created successfully\nSubject: {certificate.Subject}\nThumbprint: {certificate.Thumbprint}\nExpires: {certificate.NotAfter:u}\nStore: LocalMachine\\My";
                        return (certificate.Thumbprint, Certificates.PublicBytes(certificate));
                    }, progress, CancellationToken.None);
                    Manual(); tenant!.Text = customer.TenantId.ToString(); client!.Text = customer.ClientId.ToString(); thumbprint!.Text = customer.Thumbprint;
                    progressText.Text += "\n✓ Administrator bootstrap session ended\nVerifying certificate and app-only access inside M365CollectorService…";
                    await VerifyAndSave(customer); progressText.Text += "\n✓ Customer saved after service-side Tenant Identity verification.";
                }
                catch (Exception error) { progressText.Text += "\n" + error.Message + "\nClose this dialog to retry or use guided/manual setup."; }
                finally { complete = true; progressPanel.Controls.Add(Ui.Button("Close", () => progressDialog.Close())); }
            };
            progressDialog.ShowDialog(this);
        }
        finally { busy = false; }
    }
    private async Task VerifyAndSave(Customer customer)
    {
        if (store.GetCustomers().Any(c => c.TenantId == customer.TenantId)) throw new InvalidOperationException("This tenant already exists. Select it under All Customers.");
        var id = store.RequestConnection(customer); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested(); var request = store.Requests().Single(r => r.Id == id);
            if (request.State == "Failed") throw new InvalidOperationException(request.Error);
            if (request.State == "Verified")
            {
                store.SaveCustomer(request.Customer); JsonFile.Write(Path.Combine(paths.CustomerDirectory(customer.TenantId), "Data", "tenant-identity.json"), request.Customer.Identity);
                status.Text = "✓ Connected: " + request.Customer.Identity!.DisplayName + " • " + customer.TenantId; return;
            }
            await Task.Delay(1000, timeout.Token);
        }
    }
    private void Manual()
    {
        if (tenant != null) return;
        panel.Controls.Add(Ui.Text("Manual Entra Setup", heading: true));
        panel.Controls.Add(Ui.Text("1. Open Entra → App registrations.\n2. Create a single-tenant application: SterlingTech M365Collector.\n3. Copy the Tenant ID and Client ID below.\n4. Create a certificate here and upload its public CER.\n5. Add Microsoft Graph APPLICATION permission Organization.Read.All.\n   Why required? Read tenant name and verified domains.\n6. Grant administrator consent, then Test Connection."));
        panel.Controls.Add(Ui.Button("Open Entra Admin Center", () => Ui.Open("https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade")));
        panel.Controls.Add(Ui.Button("Copy application name", () => Clipboard.SetText("SterlingTech M365Collector")));
        tenant = Ui.Field(panel, "Tenant ID"); client = Ui.Field(panel, "Client / Application ID"); thumbprint = Ui.Field(panel, "Certificate thumbprint (LocalMachine\\My)");
        panel.Controls.Add(Ui.AsyncButton("Create certificate", () =>
        {
            Check(); var id = Guid.Parse(tenant.Text); certificate?.Dispose(); certificate = Certificates.Create(id); thumbprint.Text = certificate.Thumbprint;
            status.Text = $"Certificate created successfully\nSubject: {certificate.Subject}\nThumbprint: {certificate.Thumbprint}\nExpires: {certificate.NotAfter:u}\nStore: LocalMachine\\My"; return Task.CompletedTask;
        }, status));
        panel.Controls.Add(Ui.AsyncButton("Save Public Certificate", () =>
        {
            Check(); using var cert = Certificates.Find(thumbprint.Text.Trim());
            using var dialog = new SaveFileDialog { Filter = "Public certificate (*.cer)|*.cer", FileName = "M365Collector-" + string.Concat(name.Text.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) + "-public.cer", AddExtension = true, DefaultExt = "cer" };
            if (dialog.ShowDialog() == DialogResult.OK) Certificates.ExportPublic(cert, dialog.FileName); status.Text = "Only the public certificate is exported. The private key remains in Windows."; return Task.CompletedTask;
        }, status));
        panel.Controls.Add(Ui.AsyncButton("Test Connection & Save", async () => { Check(); await VerifyAndSave(new(Guid.Parse(tenant.Text), name.Text.Trim(), Guid.Parse(client.Text), thumbprint.Text.Trim())); }, status));
        panel.Controls.Add(Ui.Button("Copy permission name", () => Clipboard.SetText("Organization.Read.All")));
        panel.Controls.Add(Ui.AsyncButton("Guided PowerShell Setup", () =>
        {
            Check(); using var dialog = new SaveFileDialog { Filter = "PowerShell (*.ps1)|*.ps1", FileName = "M365Collector-GuidedSetup.ps1" };
            if (dialog.ShowDialog() == DialogResult.OK) File.WriteAllText(dialog.FileName, GuidedSetup.Script);
            status.Text = "Script saved. Review and run it in an elevated PowerShell window. It installs no modules automatically. Follow its prompts, then paste the printed IDs here and select Test Connection & Save."; return Task.CompletedTask;
        }, status));
        panel.Controls.Add(Ui.Button("Show detailed instructions", () =>
        {
            panel.Controls.Add(Ui.Text("Application management and Graph application consent are separate capabilities. Activate eligible PIM roles when needed. Cloud Application Administrator can manage applications, but Graph application consent generally requires Privileged Role Administrator or equivalent custom capability. Configure the bootstrap public client in Administration for automatic setup. WAM requires its broker redirect URI; browser mode requires http://localhost. Consent propagation may take several minutes; retry verification without creating another app. See docs/ENTRA-ONBOARDING.md."));
        }));
        panel.Controls.Remove(status); panel.Controls.Add(status);
    }
    protected override void Dispose(bool disposing) { if (disposing) certificate?.Dispose(); base.Dispose(disposing); }
}
