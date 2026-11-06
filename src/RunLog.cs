using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Windows.Forms;

namespace SqlResultExporter {
 sealed class RunLog {
  readonly object sync=new object();
  public readonly string DirectoryPath=ConnectionProfileStore.TestRoot == null
      ? ResolveDirectory(AppContext.BaseDirectory) : Path.Combine(ConnectionProfileStore.TestRoot,"log");

  internal static string ResolveDirectory(string applicationDirectory)
  {
   var folder = new DirectoryInfo(applicationDirectory);
   var parent = folder.Parent;
   if (folder.Name.Equals("runtime", StringComparison.OrdinalIgnoreCase) &&
       parent != null && File.Exists(Path.Combine(parent.FullName, "启动工具.cmd")))
       return Path.Combine(parent.FullName, "log");
   return Path.Combine(folder.FullName, "log");
  }
  public string CurrentPath{get{return Path.Combine(DirectoryPath,"运行日志_"+DateTime.Now.ToString("yyyyMMdd")+".log");}}
  public bool Append(string line){try{lock(sync){Directory.CreateDirectory(DirectoryPath);File.AppendAllText(CurrentPath,line+Environment.NewLine,new UTF8Encoding(false));}return true;}catch{return false;}}
  public void Clear(){lock(sync){if(File.Exists(CurrentPath))File.WriteAllText(CurrentPath,"",new UTF8Encoding(false));}}
 }
 sealed partial class MainForm {
  readonly RunLog runLog=new RunLog();
  readonly Button logDirectory=new AppButton(),clearLog=new AppButton();
  bool logFailureReported;
  string SanitizeLog(string message){
   var redactor=new Exporter();message=redactor.Redact(message,password.Text);foreach(string secret in sessionPasswords.Values)message=redactor.Redact(message,secret);
   message=Regex.Replace(message,@"(?im)\b(?:Server|Data\s+Source|Host)\s*=[^\r\n]*","[连接字符串已隐去]");
   message=Regex.Replace(message,@"(?i)mongodb(?:\+srv)?://[^\s]+","[连接字符串已隐去]");return message;
  }
  void ConfigureLogButtons(FlowLayoutPanel actions){
   SetupButton(logDirectory,"日志目录",95);SetupButton(clearLog,"清空当前日志",115);actions.Controls.AddRange(new Control[]{logDirectory,clearLog});
   logDirectory.Click+=(s,e)=>{try{Directory.CreateDirectory(runLog.DirectoryPath);Process.Start(new ProcessStartInfo{FileName=runLog.DirectoryPath,UseShellExecute=true});}catch{MessageBox.Show(this,"日志目录无法打开，请检查目录权限。","日志目录");}};
   clearLog.Click+=(s,e)=>{if(MessageBox.Show(this,"清空今天的日志文件和窗口日志显示？其他日期日志、连接配置和导出结果均保留。","清空当前日志",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){try{ClearCurrentLog();}catch{MessageBox.Show(this,"日志文件无法清空，请检查写入权限。","清空当前日志");}}};
  }
  internal void ClearCurrentLog(){if(cancellation!=null)return;runLog.Clear();logBox.Clear();logFailureReported=false;Log("当前日志已清空。");}
 }
}
