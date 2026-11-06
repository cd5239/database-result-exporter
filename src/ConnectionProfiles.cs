using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace SqlResultExporter {
 // Only non-secret connection fields are part of the persisted model.
 sealed class ConnectionProfile {
  public string Id=Guid.NewGuid().ToString("N"),Name="",Host="",Port="1433",Database="",Username="",Service="";
  public DatabaseKind Kind;
  public bool Windows,Encrypt=true,Trust,Guard=true;
  public int Timeout=300;
  public override string ToString(){return string.IsNullOrEmpty(Name)?"未保存连接 "+Id.Substring(0,4):Name;}
  public static ConnectionProfile New(DatabaseKind kind){return new ConnectionProfile{Kind=kind,Port=new[]{"1433","3306","5432","1521","27017"}[(int)kind]};}
 }
 sealed class ConnectionCatalog {public int SchemaVersion=1;public string ActiveId;public List<ConnectionProfile> Profiles=new List<ConnectionProfile>();}
 sealed class ConnectionProfileStore {
  internal static string TestRoot;
  internal static string DataRoot{get{return TestRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DatabaseResultExporter");}}
  public readonly string PathName;
  static readonly JsonSerializerOptions Json=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
  public ConnectionProfileStore(){PathName=Path.Combine(DataRoot,"connections.json");}
  static void Validate(ConnectionCatalog catalog){
   if(catalog==null||catalog.SchemaVersion!=1||catalog.Profiles==null||catalog.Profiles.Count>200)throw new InvalidDataException("连接配置版本或内容无效。");
   var ids=new HashSet<string>(StringComparer.Ordinal);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var p in catalog.Profiles){Guid id;if(p==null||!Guid.TryParse(p.Id,out id)||!ids.Add(p.Id)||!Enum.IsDefined(p.Kind)||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>80||!names.Add(p.Kind+":"+p.Name)||p.Timeout<1||p.Timeout>7200)throw new InvalidDataException("连接配置有无效或重复项目。");}
  }
  public ConnectionCatalog Load(){if(!File.Exists(PathName))return new ConnectionCatalog();if(new FileInfo(PathName).Length>1024*1024)throw new InvalidDataException("连接配置文件超过限制。");var catalog=JsonSerializer.Deserialize<ConnectionCatalog>(File.ReadAllText(PathName,Encoding.UTF8),Json);Validate(catalog);return catalog;}
  public void Save(IEnumerable<ConnectionProfile> profiles,string activeId){
   var catalog=new ConnectionCatalog{Profiles=profiles.Where(p=>!string.IsNullOrWhiteSpace(p.Name)).ToList(),ActiveId=activeId};Validate(catalog);
   Directory.CreateDirectory(Path.GetDirectoryName(PathName));string temp=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temp,JsonSerializer.Serialize(catalog,Json),new UTF8Encoding(false));File.Move(temp,PathName,true);}finally{if(File.Exists(temp))File.Delete(temp);}
  }
 }
 sealed partial class MainForm {
  readonly ComboBox profilePicker=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
  readonly List<ConnectionProfile> profiles=new List<ConnectionProfile>();
  readonly Dictionary<string,string> sessionPasswords=new Dictionary<string,string>();
  readonly Dictionary<DatabaseKind,string> lastProfile=new Dictionary<DatabaseKind,string>();
  ConnectionProfile activeProfile;
  ConnectionProfileStore profileStore;
  bool loadingProfile,profileStoreWritable=true;
  void InitializeProfiles(){
   config.Controls.Add(new Label{Text="连接配置",AutoSize=true,Padding=new Padding(0,5,0,0)},2,0);config.Controls.Add(profilePicker,3,0);
   var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
   foreach(var text in new[]{"新建","保存","删除"}){var b=new AppButton{Text=text,Width=62,Height=29};buttons.Controls.Add(b);if(text=="新建")b.Click+=(s,e)=>CreateProfile();else if(text=="保存")b.Click+=(s,e)=>SaveProfileClicked();else b.Click+=(s,e)=>DeleteProfileClicked();}
   config.Controls.Add(buttons,4,0);config.SetColumnSpan(buttons,2);
   profileStore=new ConnectionProfileStore();string loadWarning=null;ConnectionCatalog catalog=null;
   try{catalog=profileStore.Load();profiles.AddRange(catalog.Profiles);}catch(Exception){profileStoreWritable=false;loadWarning="连接配置未能读取，原文件已保留；本次仍可填写和切换临时连接。请检查："+profileStore.PathName;}
   engine.SelectedIndexChanged+=(s,e)=>{if(loadingProfile||engine.SelectedIndex<0)return;CaptureProfile();DatabaseKind kind=Kind;string id;ConnectionProfile next=lastProfile.TryGetValue(kind,out id)?profiles.FirstOrDefault(p=>p.Id==id):null;if(next==null)next=profiles.FirstOrDefault(p=>p.Kind==kind);if(next==null){next=ConnectionProfile.New(kind);profiles.Add(next);}ApplyProfile(next);PersistProfiles();};
   profilePicker.SelectedIndexChanged+=(s,e)=>{if(loadingProfile)return;var next=profilePicker.SelectedItem as ConnectionProfile;if(next==null||next==activeProfile)return;CaptureProfile();ApplyProfile(next);PersistProfiles();};
   var initial=catalog==null?null:profiles.FirstOrDefault(p=>p.Id==catalog.ActiveId);if(initial==null)initial=profiles.FirstOrDefault();if(initial==null){initial=ConnectionProfile.New(DatabaseKind.SqlServer);profiles.Add(initial);}ApplyProfile(initial);
   FormClosing+=(s,e)=>{if(e.Cancel)return;CaptureProfile();if(!PersistProfiles())e.Cancel=MessageBox.Show(this,"连接配置未能保存到磁盘，是否仍然关闭？上次已保存的配置文件保持不变。","配置未保存",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes;};
   if(loadWarning!=null)Log(loadWarning);
   Log($"工具已启动（v{Util.ToolVersion}）。连接密码仅在当前进程中保留。");
  }
  void CaptureProfile(){if(activeProfile==null)return;activeProfile.Host=host.Text;activeProfile.Port=port.Text;activeProfile.Database=db.Text;activeProfile.Username=user.Text;activeProfile.Service=service.Text;activeProfile.Windows=windows.Checked;activeProfile.Encrypt=encrypt.Checked;activeProfile.Trust=trust.Checked;activeProfile.Guard=guard.Checked;activeProfile.Timeout=(int)timeout.Value;sessionPasswords[activeProfile.Id]=password.Text;}
  void ApplyProfile(ConnectionProfile p){
   loadingProfile=true;try{activeProfile=p;engine.SelectedIndex=(int)p.Kind;host.Text=p.Host;port.Text=p.Port;db.Text=p.Database;user.Text=p.Username;service.Text=p.Service;windows.Enabled=p.Kind==DatabaseKind.SqlServer;windows.Checked=p.Kind==DatabaseKind.SqlServer&&p.Windows;user.Enabled=password.Enabled=!windows.Checked;encrypt.Checked=p.Encrypt;trust.Checked=p.Trust;guard.Checked=p.Guard;timeout.Value=p.Timeout;service.Enabled=p.Kind==DatabaseKind.Oracle||p.Kind==DatabaseKind.MongoDB;string pw;password.Text=sessionPasswords.TryGetValue(p.Id,out pw)?pw:"";
    databaseHelp.Text=p.Kind==DatabaseKind.Oracle?"数据库填 Service Name；使用 SID 则填右侧认证库/SID。只读限制请用数据库权限。":p.Kind==DatabaseKind.MongoDB?"认证库留空使用 admin；查询文件用 .mongo.json。密码仅在本次运行中保留。":"可保存多套连接并随时切换；密码仅在本次运行中保留。只读限制请用数据库权限。";
    profilePicker.Items.Clear();foreach(var item in profiles.Where(x=>x.Kind==p.Kind))profilePicker.Items.Add(item);profilePicker.SelectedItem=p;lastProfile[p.Kind]=p.Id;
   }finally{loadingProfile=false;}
  }
  bool PersistProfiles(){if(!profileStoreWritable)return true;if(!profiles.Any(p=>!string.IsNullOrEmpty(p.Name))&&!File.Exists(profileStore.PathName))return true;try{profileStore.Save(profiles,activeProfile!=null&&!string.IsNullOrEmpty(activeProfile.Name)?activeProfile.Id:null);return true;}catch(Exception){Log("连接配置保存失败，本次填写仍保留在窗口中。请检查配置目录写入权限："+Path.GetDirectoryName(profileStore.PathName));return false;}}
  internal void CreateProfile(){if(cancellation!=null)return;CaptureProfile();var p=ConnectionProfile.New(Kind);profiles.Add(p);ApplyProfile(p);}
  internal void SaveProfile(string name){
   if(cancellation!=null)return;if(!profileStoreWritable)throw new InvalidOperationException("原连接配置无法读取，为保护文件已禁止覆盖。请先检查配置文件。");
   name=(name??"").Trim();if(name.Length<1||name.Length>80)throw new InvalidOperationException("连接名称需为 1 到 80 个字符。");if(profiles.Any(p=>p!=activeProfile&&p.Kind==Kind&&string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("同一数据库类型已有同名连接，请使用其他名称。");
   CaptureProfile();string oldName=activeProfile.Name;activeProfile.Name=name;if(!PersistProfiles()){activeProfile.Name=oldName;throw new InvalidOperationException("连接配置未能保存到磁盘；本次窗口中的信息仍在。");}ApplyProfile(activeProfile);Log("连接配置已保存（不含密码）。");
  }
  void SaveProfileClicked(){
   try{string name=activeProfile.Name;if(string.IsNullOrEmpty(name)){using(var dialog=new Form{Text="保存连接配置",ClientSize=new System.Drawing.Size(380,115),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false}){var label=new Label{Text="连接名称（例如：测试库、财务只读库）",AutoSize=true,Left=12,Top=12};var input=new TextBox{Left=12,Top=36,Width=350,MaxLength=80};var ok=new AppButton(primary: true){Text="保存",Left=202,Top=75,DialogResult=DialogResult.OK};var no=new AppButton{Text="取消",Left=287,Top=75,DialogResult=DialogResult.Cancel};dialog.Controls.AddRange(new Control[]{label,input,ok,no});dialog.AcceptButton=ok;dialog.CancelButton=no;if(dialog.ShowDialog(this)!=DialogResult.OK)return;name=input.Text;}}SaveProfile(name);}catch(Exception ex){MessageBox.Show(this,ex.Message,"保存连接配置");}
  }
  void DeleteProfileClicked(){if(cancellation!=null)return;if(MessageBox.Show(this,"删除当前连接配置？导出结果和数据库内容不会改变。","删除连接配置",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;DeleteActiveProfile();}
  internal bool DeleteActiveProfile(){if(cancellation!=null)return false;var old=activeProfile;int index=profiles.IndexOf(old);profiles.Remove(old);if(!PersistProfiles()){profiles.Insert(index,old);return false;}sessionPasswords.Remove(old.Id);var next=profiles.FirstOrDefault(p=>p.Kind==Kind);if(next==null){next=ConnectionProfile.New(Kind);profiles.Add(next);}ApplyProfile(next);PersistProfiles();Log("当前连接配置已删除。");return true;}
 }
}
