using M365Collector.Contracts;
using M365Collector.Security;
using M365Collector.Storage;
namespace M365Collector.GUI;

internal sealed class RecoveryForm : Form
{
    private sealed record Choice(LocalUser User) { public override string ToString() => $"{User.Name} ({User.Role})"; }
    public RecoveryForm(CollectorStore store, AccountRecovery? recoveryOverride = null)
    {
        Ui.Style(this, "M365Collector • Recover local login", 850, 700);
        var panel = Ui.Stack(); Controls.Add(panel);
        panel.Controls.Add(Ui.Text("Reset your local password", heading: true));
        panel.Controls.Add(Ui.Text("Windows administrator recovery. Select your local username below and choose a new password. Passwords are salted, one-way hashes and cannot be viewed. Microsoft accounts, customers and certificates are preserved. This action is recorded with your Windows identity."));
        var recovery = recoveryOverride ?? new AccountRecovery(store);
        var accounts = new ComboBox { Width = 560, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var account in recovery.Accounts()) accounts.Items.Add(new Choice(account));
        if (accounts.Items.Count > 0) accounts.SelectedIndex = 0;
        panel.Controls.Add(Ui.Text("Existing local account")); panel.Controls.Add(accounts);
        var password = Ui.Field(panel, "New local password (14–1024 characters)", true);
        var confirm = Ui.Field(panel, "Confirm new password", true); var status = Ui.Text("");
        var reset = Ui.AsyncButton("Reset password", async () =>
        {
            if (accounts.SelectedItem is not Choice selected) throw new InvalidOperationException("No existing account is available to reset.");
            var value = password.Text; var confirmation = confirm.Text;
            try { await Task.Run(() => recovery.Reset(selected.User.Name, value, confirmation)); status.Text = $"Password reset for {selected.User.Name}. Close this window and sign in with your new password."; }
            finally { password.Clear(); confirm.Clear(); }
        }, status);
        panel.Controls.Add(reset); panel.Controls.Add(Ui.Button("Close", Close)); panel.Controls.Add(status);
    }
}
