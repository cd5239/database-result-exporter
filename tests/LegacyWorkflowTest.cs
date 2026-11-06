using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
namespace SqlResultExporter {
 static class WorkflowTest {
  static void Check(bool v,string name){if(!v)throw new Exception(name);}
  [STAThread]static int Main(string[] args){
   string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
   try{
    string a=Path.Combine(root,"目录 A"),b=Path.Combine(root,"目录 B");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
    string s1=Path.Combine(a,"首个.sql"),s2=Path.Combine(b,"第二个.SQL"),txt=Path.Combine(a,"忽略.txt");
    File.WriteAllText(s1,"SELECT 1");File.WriteAllText(s2,"SELECT 2");File.WriteAllText(txt,"SELECT 3");
    using(var form=new MainForm()){
     Check(form.OutputDirectory=="","empty initial output");
     var data=new DataObject(DataFormats.FileDrop,new[]{s1,s2,txt,a,Path.Combine(a,"missing.sql")});
     var enter=new DragEventArgs(data,0,0,0,DragDropEffects.Copy,DragDropEffects.None);form.ScriptsDragEnter(form,enter);
     Check(enter.Effect==DragDropEffects.Copy,"accept mixed SQL drop");form.ScriptsDragDrop(form,enter);
     Check(form.ScriptFiles.Length==2&&form.ScriptFiles[0]==s1,"drop SQL only in order");Check(form.OutputDirectory==a,"first script directory");
     form.AddScripts(new[]{s1.ToUpperInvariant(),s2});Check(form.ScriptFiles.Length==2,"case insensitive dedup");
     var invalid=new DragEventArgs(new DataObject(DataFormats.FileDrop,new[]{txt,a}),0,0,0,DragDropEffects.Copy,DragDropEffects.None);form.ScriptsDragEnter(form,invalid);Check(invalid.Effect==DragDropEffects.None,"reject non SQL and folder");
     var textDrop=new DragEventArgs(new DataObject(DataFormats.Text,"SELECT 1"),0,0,0,DragDropEffects.Copy,DragDropEffects.None);form.ScriptsDragEnter(form,textDrop);Check(textDrop.Effect==DragDropEffects.None,"reject plain text");
     var list=(ListBox)typeof(MainForm).GetField("scripts",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
     list.Items.RemoveAt(0);form.RefreshDefaultOutput();Check(form.OutputDirectory==b,"remove first follows next");
     form.OutputDirectory=a;form.ClearScripts();form.AddScripts(new[]{s2});Check(form.OutputDirectory==a,"manual override persists");
     form.UseScriptDirectory();Check(form.OutputDirectory==b,"restore auto output");form.ClearScripts();Check(form.OutputDirectory=="","clear auto output");
     list.Enabled=false;form.ScriptsDragEnter(form,enter);Check(enter.Effect==DragDropEffects.None,"disabled list rejects drag");form.ScriptsDragDrop(form,enter);Check(form.ScriptFiles.Length==0,"busy drop no mutation");list.Enabled=true;
     form.AddScripts(new[]{s1});string blocked=Path.Combine(a,"blocked.sql");File.WriteAllText(blocked,"DELETE FROM dbo.example;");
     string zip=new Exporter().Run(new Options{ConnectionString="Server=unused;Database=Test;Integrated Security=true",Files=new[]{blocked},Timeout=1,Guard=true},form.OutputDirectory,CancellationToken.None,x=>{});
     Check(File.Exists(zip)&&Path.GetDirectoryName(zip)==a,"ZIP in selected script directory without Results subfolder");
    }
    File.WriteAllText(Path.Combine(root,"workflow.ok.txt"),"PASS: mixed multi-file drag; SQL-only filtering; order; duplicate paths; first-directory default; removing first; manual override; reset auto; clear; busy rejects drop; export ZIP in script directory. No live database access.");return 0;
   }catch(Exception e){File.WriteAllText(Path.Combine(root,"workflow.failed.txt"),e.ToString());return 1;}
  }
 }
}
