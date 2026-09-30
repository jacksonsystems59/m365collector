using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.GUI;
using Xunit;
namespace M365Collector.Tests;
public class GuiTests
{
    [Theory][InlineData("file:///C:/Windows/System32/cmd.exe")][InlineData("https://attacker.example/payload")]
    public void ExternalLinksRejectUnexpectedTargets(string url) => Assert.Throws<InvalidDataException>(() => Ui.Open(url));
    [Fact] public void WizardAndCustomerScreensRenderWithoutCredentials()
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            try
            {
                using var temp=new Scratch(); var store=temp.Database(); var screenshots=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../dist/screenshots"));Directory.CreateDirectory(screenshots);
                using var wizard=new FirstRunWizard(); Render(wizard,Path.Combine(screenshots,"first-run.png"));Assert.Contains("First Run",wizard.Text);
                using var host=new Form{Width=1100,Height=850};using var customer=new CustomerPage(store,new LocalUser("Administrator",LocalRole.Administrator),new RuntimePaths(temp.At("data")));host.Controls.Add(customer);Render(host,Path.Combine(screenshots,"add-customer.png"));
                var tenant=Guid.NewGuid();store.SaveCustomer(new Customer(tenant,"Example Customer",Guid.NewGuid(),"fixture"){Identity=new(tenant,"Example Customer",[],DateTimeOffset.UtcNow)});
                JsonFile.Write(temp.At("data/Cache/release-check.json"),new ReleaseCache(DateTimeOffset.UtcNow,null,null));
                using var main=new MainForm(new Installation(temp.At("data"),temp.At("app"),true),store,new LocalUser("Administrator",LocalRole.Administrator));
                Render(main,Path.Combine(screenshots,"dashboard-v2.png"));
                Render(main,Path.Combine(screenshots,"modules-v2.png"),()=>Find(main).OfType<Button>().Single(b=>b.Text=="Collection Modules").PerformClick());
                Render(main,Path.Combine(screenshots,"explorer-v2.png"),()=>Find(main).OfType<Button>().Single(b=>b.Text=="Audit Explorer").PerformClick());
                main.Height=760;main.PerformLayout();var version=Find(main).Single(c=>c.Name=="SidebarVersion");var lockControl=Find(main).Single(c=>c.Name=="SidebarLock");
                Assert.Contains("0.0.2",version.Text);Assert.True(version.PointToScreen(Point.Empty).Y>main.PointToScreen(Point.Empty).Y+main.ClientSize.Height-100);Assert.True(lockControl.PointToScreen(Point.Empty).X<main.PointToScreen(Point.Empty).X+235);
            }
            catch(Exception e){error=e;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));Assert.Null(error);
    }
    private static IEnumerable<Control> Find(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Find(c)));
    private static void Render(Form form,string path,Action? navigate=null) { form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-30000,-30000);form.Opacity=0;form.Show();Application.DoEvents();navigate?.Invoke();Application.DoEvents();using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);form.Hide(); }
}
