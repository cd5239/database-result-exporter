using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
namespace SqlResultExporter {
 static class ProfileTests {
  static int count;
  static T Field<T>(MainForm form,string name){return (T)typeof(MainForm).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);}
  static void Text(MainForm form,string name,string value){Field<TextBox>(form,name).Text=value;}
  static string Text(MainForm form,string name){return Field<TextBox>(form,name).Text;}
  static void Check(bool pass,string name){if(!pass)throw new Exception("Profile/log: "+name);count++;}
  static void Reject(Action action,string name){try{action();}catch{count++;return;}throw new Exception("Expected rejection: "+name);}
  static void Pick(MainForm form,string name){var picker=Field<ComboBox>(form,"profilePicker");picker.SelectedItem=picker.Items.Cast<ConnectionProfile>().First(p=>p.Name==name);}
  static void Engine(MainForm form,DatabaseKind kind){Field<ComboBox>(form,"engine").SelectedIndex=(int)kind;}
  public static int Run(string root){
   count=0;string previous=ConnectionProfileStore.TestRoot;ConnectionProfileStore.TestRoot=Path.Combine(root,"profiles",Guid.NewGuid().ToString("N"));
   string storePath=Path.Combine(ConnectionProfileStore.DataRoot,"connections.json");
   string a="test-A;@密码",b="test-B!",m="test-M;";
   try{
    using(var f=new MainForm()){
     var layout=Field<TableLayoutPanel>(f,"config");Check(layout.GetRow(Field<ComboBox>(f,"engine"))==0&&layout.GetRow(Field<TextBox>(f,"host"))==1,"type above server");
     Text(f,"host","sql-a.example");Text(f,"port","1544");Text(f,"db","DbA");Text(f,"user","readerA");Text(f,"password",a);Field<NumericUpDown>(f,"timeout").Value=123;Field<CheckBox>(f,"trust").Checked=true;Field<CheckBox>(f,"guard").Checked=false;f.SaveProfile("SQL A");
     Text(f,"host","sql-a-updated.example");f.CreateProfile();Check(Text(f,"host")==""&&Text(f,"password")=="","new profile blank");
     Text(f,"host","sql-b.example");Text(f,"db","DbB");Text(f,"user","readerB");Text(f,"password",b);f.SaveProfile("SQL B");Pick(f,"SQL A");
     Check(Text(f,"host")=="sql-a-updated.example"&&Text(f,"db")=="DbA"&&Text(f,"port")=="1544","same type multiple endpoint restore");
     Check(Text(f,"password")==a&&Text(f,"user")=="readerA","session password isolated A");
     Check(Field<NumericUpDown>(f,"timeout").Value==123&&Field<CheckBox>(f,"trust").Checked&&!Field<CheckBox>(f,"guard").Checked,"options restore");
     Engine(f,DatabaseKind.MySQL);Check(Text(f,"host")==""&&Text(f,"port")=="3306"&&Text(f,"password")=="","new type defaults");
     Text(f,"host","mysql.example");Text(f,"db","DbM");Text(f,"user","readerM");Text(f,"password",m);f.SaveProfile("MySQL A");Engine(f,DatabaseKind.SqlServer);
     Check(Text(f,"host")=="sql-a-updated.example"&&Text(f,"password")==a,"type switch restores last SQL profile");Pick(f,"SQL B");Check(Text(f,"password")==b&&Text(f,"db")=="DbB","session password isolated B");
     Engine(f,DatabaseKind.MySQL);Check(Text(f,"password")==m&&Text(f,"host")=="mysql.example","type switch restores MySQL");
     string json=File.ReadAllText(storePath);Check(!json.Contains(a)&&!json.Contains(b)&&!json.Contains(m)&&!json.Contains(Uri.EscapeDataString(a)),"no secret values on disk");
     Check(!json.Contains("Password",StringComparison.OrdinalIgnoreCase)&&!json.Contains("ConnectionString",StringComparison.OrdinalIgnoreCase),"no secret schema fields");
     var logMethod=typeof(MainForm).GetMethod("Log",BindingFlags.NonPublic|BindingFlags.Instance);logMethod.Invoke(f,new object[]{"operation "+a+" "+b+" "+Uri.EscapeDataString(m)});logMethod.Invoke(f,new object[]{"Server=private-host;Database=Db;User ID=example;Password="+a});
     var logger=Field<RunLog>(f,"runLog");string log=File.ReadAllText(logger.CurrentPath);Check(!log.Contains(a)&&!log.Contains(b)&&!log.Contains(Uri.EscapeDataString(m))&&!log.Contains("private-host"),"disk logs redact all sessions and connection strings");Check(Field<TextBox>(f,"logBox").Text.Contains("operation"),"window log preserved");
     string toolRoot=Path.Combine(root,"日志路径验证"), runtime=Path.Combine(toolRoot,"runtime");
     Directory.CreateDirectory(runtime);
     File.WriteAllText(Path.Combine(toolRoot,"启动工具.cmd"),"@echo off");
     Check(RunLog.ResolveDirectory(runtime)==Path.Combine(toolRoot,"log"),"packaged logs are beside the launcher");
     Check(RunLog.ResolveDirectory(toolRoot)==Path.Combine(toolRoot,"log"),"standalone logs are beside the executable");
     string oldLog=Path.Combine(logger.DirectoryPath,"运行日志_20000101.log");File.WriteAllText(oldLog,"old-day");string result=Path.Combine(root,"untouched-result.json");File.WriteAllText(result,"keep");f.ClearCurrentLog();Check(!File.ReadAllText(logger.CurrentPath).Contains("operation")&&!Field<TextBox>(f,"logBox").Text.Contains("operation"),"current log and window cleared");Check(File.ReadAllText(oldLog)=="old-day"&&File.ReadAllText(result)=="keep"&&File.Exists(storePath),"clear preserves other logs configs and results");
     typeof(MainForm).GetMethod("Busy",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f,new object[]{true});Check(!Field<ComboBox>(f,"engine").Enabled&&!Field<ComboBox>(f,"profilePicker").Enabled&&!Field<Button>(f,"clearLog").Enabled,"busy prevents configuration switch and clear");typeof(MainForm).GetMethod("Busy",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f,new object[]{false});
    }
    using(var f=new MainForm()){
     Check(Text(f,"host")=="mysql.example"&&Text(f,"db")=="DbM"&&Text(f,"user")=="readerM","saved active config survives restart");Check(Text(f,"password")=="","restart does not restore password");
     Engine(f,DatabaseKind.SqlServer);Pick(f,"SQL A");Check(Text(f,"host")=="sql-a-updated.example"&&Text(f,"password")=="","saved edits survive restart without password");
     string original=File.ReadAllText(storePath);f.CreateProfile();Reject(()=>f.SaveProfile("sql a"),"case insensitive duplicate name");Check(File.ReadAllText(storePath)==original,"duplicate name does not alter stored configuration");
     Pick(f,"SQL B");Check(f.DeleteActiveProfile(),"delete named profile");Check(!new ConnectionProfileStore().Load().Profiles.Any(p=>p.Name=="SQL B"),"delete persists");
     Engine(f,DatabaseKind.Oracle);Text(f,"host","oracle.example");Text(f,"service","SAMPLE_SID");Text(f,"password","ephemeral");Engine(f,DatabaseKind.MongoDB);Text(f,"service","authdb");Engine(f,DatabaseKind.Oracle);Check(Text(f,"service")=="SAMPLE_SID"&&Text(f,"password")=="ephemeral","unsaved type draft also retained");Engine(f,DatabaseKind.MongoDB);Check(Text(f,"service")=="authdb","Mongo auth source retained");
     f.Show();Application.DoEvents();using(var bmp=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save(Path.Combine(root,"profiles-window.png"));}f.Hide();
    }
    using(var f=new MainForm()){Engine(f,DatabaseKind.SqlServer);Pick(f,"SQL A");Text(f,"host","saved-on-close.example");Text(f,"password","only-this-process");f.Show();Application.DoEvents();f.Close();}
    using(var f=new MainForm()){Check(Text(f,"host")=="saved-on-close.example","normal close persists edited saved profile");Check(Text(f,"password")=="","close does not persist password");}
    ConnectionProfileStore.TestRoot=Path.Combine(root,"corrupt-config");Directory.CreateDirectory(ConnectionProfileStore.DataRoot);string broken=Path.Combine(ConnectionProfileStore.DataRoot,"connections.json");File.WriteAllText(broken,"malformed-json");using(var f=new MainForm()){Text(f,"host","temp.example");Engine(f,DatabaseKind.MySQL);Engine(f,DatabaseKind.SqlServer);Check(Text(f,"host")=="temp.example","temporary switching works with corrupt store");Reject(()=>f.SaveProfile("Do not overwrite"),"corrupt store save rejected");Check(File.ReadAllText(broken)=="malformed-json","corrupt file preserved");}
    ConnectionProfileStore.TestRoot=Path.Combine(root,"blocked-data-root");File.WriteAllText(ConnectionProfileStore.TestRoot,"keep-original");using(var f=new MainForm()){Text(f,"host","temporary-endpoint");Reject(()=>f.SaveProfile("Cannot write"),"save write failure is reported");Check(Text(f,"host")=="temporary-endpoint"&&File.ReadAllText(ConnectionProfileStore.TestRoot)=="keep-original","failed save preserves in-memory values and original file");Check(Field<TextBox>(f,"logBox").Text.Contains("当前仍显示窗口日志"),"disk log failure falls back to window without aborting");}
    return count;
   }finally{ConnectionProfileStore.TestRoot=previous;}
  }
 }
}
