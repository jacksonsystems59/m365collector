using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Storage;
namespace M365Collector.GUI;
internal sealed partial class MainForm
{
    private sealed record SourceChoice(string Name,string Module,string Workload) { public override string ToString()=>Name; }
    private void Explorer(Guid? tenant=null,string module="",string result="",string group="Events")
    {
        var page=Page("Audit Explorer");page.Controls.Add(Ui.Text("Search locally collected data. Dates are UTC; the end time is exclusive. Double-click an event to inspect its original Microsoft JSON."));
        Customer? selected=null;CustomerPicker(page,customer=>selected=customer,tenant);
        var filters=new FlowLayoutPanel{AutoSize=true,MaximumSize=new Size(980,0),WrapContents=true};page.Controls.Add(filters);
        Control Field(string title,Control control)
        {
            var holder=new FlowLayoutPanel{Width=225,Height=72,FlowDirection=FlowDirection.TopDown,WrapContents=false};holder.Controls.Add(new Label{Text=title,AutoSize=true});control.Width=210;holder.Controls.Add(control);filters.Controls.Add(holder);return control;
        }
        var from=(DateTimePicker)Field("From UTC",new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm",Value=DateTime.UtcNow.AddDays(-1)});
        var to=(DateTimePicker)Field("To UTC",new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm",Value=DateTime.UtcNow});
        var type=(ComboBox)Field("Data type",new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList});
        type.Items.AddRange(new SourceChoice[]{new("All","",""),new("Sign-ins","sign-ins",""),new("Directory Changes","directory-changes",""),new("SharePoint / OneDrive","unified-audit","SharePoint / OneDrive"),new("Exchange Audit","unified-audit","Exchange"),new("Teams","unified-audit","Teams"),new("General Microsoft 365 Audit","unified-audit","General Microsoft 365"),new("All Unified Audit","unified-audit","")});
        type.SelectedIndex=module=="sign-ins"?1:module=="directory-changes"?2:module=="unified-audit"?7:0;
        var userFilter=(TextBox)Field("User / display name",new TextBox());var action=(TextBox)Field("Action / operation",new TextBox());var obj=(TextBox)Field("File / object / target",new TextBox());var site=(TextBox)Field("Site / folder",new TextBox());var ip=(TextBox)Field("IP",new TextBox());var location=(TextBox)Field("Country / state / city",new TextBox());var app=(TextBox)Field("Application",new TextBox());var category=(TextBox)Field("Category / record type",new TextBox());
        var outcome=(ComboBox)Field("Result",new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList});outcome.Items.AddRange(["All","Successful","Failed","Interrupted","Unknown"]);outcome.SelectedItem=result.Length>0?result:"All";
        var grouping=(ComboBox)Field("View / group by",new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList});grouping.Items.AddRange(["Events","User","IP / location","Country","State","City"]);grouping.SelectedItem=group;
        var status=Ui.Text("");var buttons=new FlowLayoutPanel{AutoSize=true};page.Controls.Add(buttons);page.Controls.Add(status);
        var grid=new DataGridView{Width=970,Height=360,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoGenerateColumns=true,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.DisplayedCells,RowHeadersVisible=false};page.Controls.Add(grid);
        var detail=new TextBox{Width=970,Height=260,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Visible=false};page.Controls.Add(detail);
        IReadOnlyList<StoredEvent> rows=[];AuditFilter? applied=null;var offset=0;long total=0;string appliedGroup="Events";
        AuditFilter ReadFilter()
        {
            if(selected==null)throw new InvalidOperationException("Select a customer first.");var source=(SourceChoice)type.SelectedItem!;
            return new(selected.TenantId,new DateTimeOffset(DateTime.SpecifyKind(from.Value,DateTimeKind.Utc)),new DateTimeOffset(DateTime.SpecifyKind(to.Value,DateTimeKind.Utc)),source.Module,source.Workload,userFilter.Text,action.Text,obj.Text,site.Text,ip.Text,location.Text,outcome.Text=="All"?"":outcome.Text,app.Text,category.Text);
        }
        async Task Search(bool reset)
        {
            if(reset){applied=ReadFilter();offset=0;appliedGroup=grouping.Text;}if(applied==null)return;
            var filter=applied;detail.Visible=false;
            if(appliedGroup!="Events")
            {
                var groups=await Task.Run(()=>store.Groups(filter,appliedGroup));rows=[];grid.DataSource=groups.ToList();status.Text=$"{groups.Count} groups shown (maximum 500), grouped across all matching local records. CSV exports matching events.";return;
            }
            var found=await Task.Run(()=>(store.Count(filter),store.Search(filter,offset,250)));total=found.Item1;rows=found.Item2;
            grid.DataSource=rows.Select(e=>new{TimestampUTC=e.Timestamp.UtcDateTime,User=e.User,Source=e.ModuleId,Workload=e.Workload,Action=e.Action,ObjectFile=e.Object,SiteApplication=e.Site.Length>0?e.Site:e.Application,IP=e.Ip,Location=string.Join(" / ",new[]{e.Country,e.State,e.City}.Where(s=>s.Length>0)),Result=e.Result}).ToList();
            status.Text=$"{total:N0} matching events • showing {(rows.Count==0?0:offset+1):N0}–{offset+rows.Count:N0}. Search uses the local database.";
        }
        buttons.Controls.Add(Ui.AsyncButton("Search",()=>Search(true),status));buttons.Controls.Add(Ui.Button("Clear Filters",()=>Explorer(selected?.TenantId)));
        buttons.Controls.Add(Ui.AsyncButton("Previous",async()=>{offset=Math.Max(0,offset-250);await Search(false);},status));buttons.Controls.Add(Ui.AsyncButton("Next",async()=>{if(offset+250<total)offset+=250;await Search(false);},status));
        buttons.Controls.Add(Ui.AsyncButton("Export filtered CSV",async()=>
        {
            if(applied==null)throw new InvalidOperationException("Run Search first. The export uses the last applied filters.");
            using var dialog=new SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName=$"M365Collector-{applied.TenantId}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv"};if(dialog.ShowDialog()!=DialogResult.OK)return;
            var filter=applied;var customer=store.GetCustomers().Single(c=>c.TenantId==filter.TenantId).Name;var count=await Task.Run(()=>CsvExport.ExportAsync(store,filter,customer,dialog.FileName,CancellationToken.None));status.Text=$"Exported {count:N0} matching events. CSV includes customer/date metadata and spreadsheet formula protection.";
        },status));
        grid.CellDoubleClick+=(_,e)=>
        {
            if(e.RowIndex<0||e.RowIndex>=rows.Count)return;var item=rows[e.RowIndex];using var raw=JsonDocument.Parse(item.RawJson);
            detail.Text=$"EVENT DETAILS\r\nTenant: {item.TenantId}\r\nEvent: {item.EventId}\r\nTimestamp UTC: {item.Timestamp:O}\r\nUser: {item.User} ({item.UserDisplayName})\r\nSource: {item.ModuleId} / {item.Workload}\r\nAction: {item.Action}\r\nObject: {item.Object}\r\nSite/folder: {item.Site}\r\nApplication: {item.Application}\r\nIP/location: {item.Ip} / {item.Country} / {item.State} / {item.City}\r\nResult: {item.Result}\r\n\r\nRAW MICROSOFT EVENT (text only)\r\n"+JsonSerializer.Serialize(raw.RootElement,new JsonSerializerOptions{WriteIndented=true});detail.Visible=true;
        };
        page.Controls.Add(Ui.Button("Hide event details",()=>detail.Visible=false));
    }
}
