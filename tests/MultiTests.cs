using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MongoDB.Bson;
namespace SqlResultExporter {
 static class MultiTests {
  static int count;
  static void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
  static void Reject(Action a,string name){try{a();}catch(Exception){count++;return;}throw new Exception("Expected rejection: "+name);}
  [STAThread]static int Main(string[] args){try{Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);System.Globalization.CultureInfo.DefaultThreadCurrentCulture=System.Globalization.CultureInfo.InvariantCulture;string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
   ConnectionProfileStore.TestRoot=Path.Combine(root,"appdata");
   SelfTest.Run(Path.Combine(root,"original-regression"));
   Check(Dialect.Split("select ';'; select 2;",DatabaseKind.MySQL).Count==2,"MySQL quoted separator");
   Check(Dialect.Split("select $$a;b$$; select $tag$x;y$tag$;",DatabaseKind.PostgreSQL).Count==2,"Postgres dollar quote");
   Check(Dialect.Split("BEGIN\n NULL;\nEND;\n/\nSELECT 1 FROM dual;",DatabaseKind.Oracle).Count==2,"Oracle PL/SQL block");
   Check(Dialect.Split("SELECT 'a;b' FROM dual;SELECT 2 FROM dual;",DatabaseKind.Oracle).Count==2,"Oracle select batches");
   Check(Dialect.Split("SELECT 'a\n/\nb' FROM dual;",DatabaseKind.Oracle).Count==1,"Oracle slash in multiline literal");
   Reject(()=>Dialect.Split("select E'a\\';x';",DatabaseKind.PostgreSQL),"unsupported PG escape literal rejected");
   Reject(()=>Dialect.Split("DELIMITER //\nSELECT 1",DatabaseKind.MySQL),"mysql client command");
   Reject(()=>Dialect.Split("select 'unfinished",DatabaseKind.PostgreSQL),"unclosed literal");
   string password="synthetic;!@密码";
   Check(!new Exporter().Redact("mongodb://user:"+Uri.EscapeDataString(password)+"@host/db",password).Contains(Uri.EscapeDataString(password)),"URI credential redaction");
   foreach(DatabaseKind kind in Enum.GetValues(typeof(DatabaseKind))){string cs=Providers.Connection(kind,"localhost","1234","ExampleDb","example",password,false,false,false,"");var options=new Options{Kind=kind,ConnectionString=cs,Password=password};if(kind!=DatabaseKind.MongoDB)using(var c=Providers.Create(options)){Check(c!=null,kind+" driver load");}Check(!new Exporter().Redact("test "+password,password).Contains(password),kind+" redact");}
   Reject(()=>Providers.Connection(DatabaseKind.Oracle,"localhost","1521","service","u","p",false,true,true,""),"Oracle unsupported skip TLS validation");
   var oracleTarget=Providers.Target(new Options{Kind=DatabaseKind.Oracle,ConnectionString=Providers.Connection(DatabaseKind.Oracle,"localhost","1521","service","example","p",false,false,false,"")});Check(oracleTarget[0]=="localhost"&&oracleTarget[1]=="service","Oracle manifest target");
   MongoExport.Query("{collection:'example',operation:'find',filter:{active:true}}");
   Reject(()=>MongoExport.Query("{collection:'example',operation:'delete'}"),"Mongo operation");
   Reject(()=>MongoExport.Query("{collection:'example',operation:'aggregate',pipeline:[],limit:10}"),"Mongo ignored option rejected");
   Reject(()=>MongoExport.Query("{collection:'example',operation:'aggregate',pipeline:[{$facet:{x:[{$merge:'bad'}]}}]}"),"Mongo nested write");
   var documents=new[]{new BsonDocument{{"_id",ObjectId.GenerateNewId()},{"amount",Decimal128.Parse("12345678901234567890.12345678901234")},{"nested",new BsonDocument("items",new BsonArray{1,2})},{"text","=1+1"}},new BsonDocument{{"later","new"},{"null",BsonNull.Value}}};
   string fd=Path.Combine(root,"mongo");Directory.CreateDirectory(fd);var results=new List<ResultInfo>();MongoExport.WriteDocuments(documents,fd,results,CancellationToken.None,s=>{});
   Check(results[0].complete&&results[0].rowCount==2&&results[0].columnCount==6,"Mongo union columns");
   var actual=BsonDocument.Parse(File.ReadLines(Path.Combine(fd,results[0].jsonl)).First());Check(actual.Equals(documents[0]),"Mongo exact BSON roundtrip");
   Check(File.ReadAllText(Path.Combine(fd,results[0].csv)).Contains("'=1+1"),"Mongo CSV formula escaping");
   var cts=new CancellationTokenSource();string partial=Path.Combine(root,"mongo-partial");Directory.CreateDirectory(partial);var rs=new List<ResultInfo>();
   Reject(()=>MongoExport.WriteDocuments(CancelDocuments(documents,cts),partial,rs,cts.Token,s=>{}),"Mongo cancellation");Check(rs.Count==1&&rs[0].rowCount==1&&!rs[0].complete,"Mongo partial retained");
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   count+=ProfileTests.Run(root);
   count+=ExcelImportTests.Run(root);
   count+=KeywordSearchTests.Run(root);
   using(var f=new MainForm()){f.Show();Application.DoEvents();var db=(TextBox)typeof(MainForm).GetField("db",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);Check(db.Text=="","empty database default");using(var bmp=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(bmp,new Rectangle(Point.Empty,bmp.Size));bmp.Save(Path.Combine(root,"window.png"));}f.Hide();}
   typeof(WorkflowTest).GetMethod("Main",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{new[]{Path.Combine(root,"workflow")}});
   Check(File.Exists(Path.Combine(root,"workflow","workflow.ok.txt")),"inherited drag/output workflow");
   File.WriteAllText(Path.Combine(root,"tests.ok.txt"),"PASS "+count+" checks + inherited exporter selftest and drag/output workflow; no live database success-path validation.");Console.WriteLine("PASS "+count);return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
  static IEnumerable<BsonDocument> CancelDocuments(BsonDocument[] docs,CancellationTokenSource cts){yield return docs[0];cts.Cancel();yield return docs[1];}
 }
}
