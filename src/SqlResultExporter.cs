using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SqlResultExporter {
 static class Util {
  internal static readonly string ToolVersion = typeof(Util).Assembly.GetName().Version.ToString(3);
  public static readonly Encoding Utf8 = new UTF8Encoding(false);
  public static Dictionary<string,object> Obj(params object[] args) {
   var d=new Dictionary<string,object>(); for(int i=0;i<args.Length;i+=2)d.Add((string)args[i],args[i+1]); return d;
  }
  public static string Json(object value) { return new JavaScriptSerializer{MaxJsonLength=int.MaxValue,RecursionLimit=100}.Serialize(value); }
  public static void Save(string path,object value) {File.WriteAllText(path,Json(value),Utf8);}
  public static string Safe(string s) {foreach(char c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_'); s=s.Trim().TrimEnd('.'); return s.Length>75?s.Substring(0,75):s;}
  public static string Hash(string path) {using(var f=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
  public static string ReadSql(string path) {
   byte[] b=File.ReadAllBytes(path);
   if(b.Length>=2 && ((b[0]==255&&b[1]==254)||(b[0]==254&&b[1]==255)))return File.ReadAllText(path,Encoding.Unicode);
   if(b.Length>=3&&b[0]==239&&b[1]==187&&b[2]==191)return new UTF8Encoding(false,true).GetString(b,3,b.Length-3);
   try{return new UTF8Encoding(false,true).GetString(b);}catch(DecoderFallbackException){return Encoding.GetEncoding(936).GetString(b);}
  }
  public static string Value(object v) {
   if(v==null||v==DBNull.Value)return null;
   if(v is INullable && ((INullable)v).IsNull)return null;
   if(v is SqlDecimal)return ((SqlDecimal)v).ToString();
   if(v is SqlString)return ((SqlString)v).Value;
   if(v is SqlChars)return new string(((SqlChars)v).Value);
   if(v is SqlXml)return ((SqlXml)v).Value;
   if(v is SqlBinary)return Convert.ToBase64String(((SqlBinary)v).Value);
   if(v is SqlBytes)return Convert.ToBase64String(((SqlBytes)v).Value);
   if(v is SqlInt64)v=((SqlInt64)v).Value;else if(v is SqlInt32)v=((SqlInt32)v).Value;
   else if(v is SqlInt16)v=((SqlInt16)v).Value;else if(v is SqlByte)v=((SqlByte)v).Value;
   else if(v is SqlMoney)v=((SqlMoney)v).Value;else if(v is SqlDouble)v=((SqlDouble)v).Value;
   else if(v is SqlSingle)v=((SqlSingle)v).Value;else if(v is SqlBoolean)v=((SqlBoolean)v).Value;
   else if(v is SqlDateTime)v=((SqlDateTime)v).Value;else if(v is SqlGuid)v=((SqlGuid)v).Value;
   if(v is byte[])return Convert.ToBase64String((byte[])v);
   if(v is DateTime)return ((DateTime)v).ToString("yyyy-MM-ddTHH:mm:ss.fffffff",CultureInfo.InvariantCulture);
   if(v is DateTimeOffset)return ((DateTimeOffset)v).ToString("o",CultureInfo.InvariantCulture);
   if(v is TimeSpan)return ((TimeSpan)v).ToString("c",CultureInfo.InvariantCulture);
   if(v is double)return ((double)v).ToString("R",CultureInfo.InvariantCulture);
   if(v is float)return ((float)v).ToString("R",CultureInfo.InvariantCulture);
   return Convert.ToString(v,CultureInfo.InvariantCulture);
  }
  public static string Csv(object original,string value) {
   if(value==null)return "";
   // CSV方便Excel查看；JSONL保留原值。文本公式加单引号，避免Excel按公式执行。
   if((original is string || original is SqlString || original is SqlChars) && value.Length>0 && "=+-@\t\r\n".IndexOf(value[0])>=0)value="'"+value;
   return "\""+value.Replace("\"","\"\"")+"\"";
  }
 }
 public sealed class Batch {public string Text;public int StartLine;}
 static class SqlText {
  // 识别字符串、双引号标识符、方括号及嵌套块注释，GO只在普通状态独占一行时生效。
  public static List<Batch> Split(string text) {
   var batches=new List<Batch>(); var buf=new StringBuilder();int state=0,depth=0,lineNo=0,start=1;
   using(var sr=new StringReader(text)) { string line;
    while((line=sr.ReadLine())!=null) {
     lineNo++;
     if(state==0) {
      var go=Regex.Match(line,@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$",RegexOptions.IgnoreCase);
      if(go.Success) {
       int repeats=go.Groups[1].Success?int.Parse(go.Groups[1].Value,CultureInfo.InvariantCulture):1;
       if(repeats<1||repeats>100)throw new InvalidOperationException("GO重复次数仅支持1到100：第"+lineNo+"行。");
       if(buf.ToString().Trim().Length>0)for(int k=0;k<repeats;k++)batches.Add(new Batch{Text=buf.ToString(),StartLine=start});
       buf.Clear();start=lineNo+1;continue;
      }
      if(Regex.IsMatch(line,@"^\s*(?::|!!)"))throw new InvalidOperationException("不支持SQLCMD的:r、:setvar、!!等指令，请先展开为普通SQL。行："+lineNo);
     }
     buf.AppendLine(line);
     for(int i=0;i<line.Length;i++) {
      char c=line[i],n=i+1<line.Length?line[i+1]:'\0';
      if(state==1){if(c=='\''){if(n=='\'')i++;else state=0;}continue;}
      if(state==2){if(c=='\"'){if(n=='\"')i++;else state=0;}continue;}
      if(state==3){if(c==']'){if(n==']')i++;else state=0;}continue;}
      if(state==4){if(c=='/'&&n=='*'){depth++;i++;}else if(c=='*'&&n=='/'){depth--;i++;if(depth==0)state=0;}continue;}
      if(c=='-'&&n=='-')break;
      if(c=='/'&&n=='*'){state=4;depth=1;i++;}else if(c=='\'')state=1;else if(c=='\"')state=2;else if(c=='[')state=3;
     }
    }
   }
   if(state!=0)throw new InvalidOperationException("SQL存在未闭合的字符串、标识符或块注释。");
   if(buf.ToString().Trim().Length>0)batches.Add(new Batch{Text=buf.ToString(),StartLine=start});
   return batches;
  }
  public static string Guard(string sql) {
   // 辅助静态检查，非安全沙箱。保留字符串内容，检查常见动态SQL写入；可能误报。
   var s=new StringBuilder();int state=0,depth=0;
   for(int i=0;i<sql.Length;i++) {
    char c=sql[i],n=i+1<sql.Length?sql[i+1]:'\0';
    if(state==4){if(c=='/'&&n=='*'){depth++;i++;}else if(c=='*'&&n=='/'){if(--depth==0)state=0;i++;}s.Append(' ');continue;}
    if(state==5){if(c=='\n'){state=0;s.Append('\n');}else s.Append(' ');continue;}
    if(state==1){s.Append(c);if(c=='\''){if(n=='\''){s.Append(n);i++;}else state=0;}continue;}
    if(state==0&&c=='\''){state=1;s.Append(c);continue;}
    if(c=='-'&&n=='-'){state=5;i++;s.Append("  ");continue;}
    if(c=='/'&&n=='*'){state=4;depth=1;i++;s.Append("  ");continue;}
    s.Append(c);
   }
   var m=Regex.Match(s.ToString(),@"\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE|DENY|BACKUP|RESTORE|DBCC|BULK)\b",RegexOptions.IgnoreCase);
   if(m.Success)return "检测到关键字 "+m.Value+"。当前开启常见写入拦截。请核对脚本；本工具默认用于查询核查。";
   if(Regex.IsMatch(s.ToString(),@"\bINTO\s+(?!#)[\[\w]",RegexOptions.IgnoreCase))return "检测到可能的SELECT INTO永久表语句，请核对脚本。";
   if(Regex.IsMatch(s.ToString(),@"\bEXEC(?:UTE)?\s+(?!(?:sys\.)?sp_executesql\b)",RegexOptions.IgnoreCase))return "检测到存储过程执行；辅助检查仅放行sp_executesql。请先核对脚本。";
   return null;
  }
 }
 public sealed class ResultInfo {
  public int index,batch,columnCount;public long rowCount;public string schema,jsonl,csv;
  public bool complete,reportedError;public List<Dictionary<string,object>> columns;
 }
 public sealed class FileInfoResult {
  public string name,sha256,status,error;public int batchCount,completedBatches;public long elapsedMs;
  public List<ResultInfo> resultSets=new List<ResultInfo>();public List<Dictionary<string,object>> sqlErrors=new List<Dictionary<string,object>>();
 }
 sealed class Options {
  public string ConnectionString,Password;public DatabaseKind Kind;public string[] Files;public int Timeout;public bool Guard;
 }
 sealed class Exporter {
  public DbCommand ActiveCommand; public readonly object CommandLock=new object();
  public void Cancel(){lock(CommandLock){if(ActiveCommand!=null)try{ActiveCommand.Cancel();}catch{}}}
  public string Redact(string text,string password) {if(text==null)return null;if(!string.IsNullOrEmpty(password)){text=text.Replace(password,"[密码已隐去]").Replace(Uri.EscapeDataString(password),"[密码已隐去]");}text=Regex.Replace(text,@"(?i)(mongodb(?:\+srv)?://)[^@\s]+@","$1[认证信息已隐去]@");text=Regex.Replace(text,@"(?i)\b(password|pwd)\s*=\s*(?:""(?:[^""]|"""")*""|'(?:[^']|'')*'|[^;\r\n]*)","$1=[密码已隐去]");return text;}
  public static void WriteReader(DbDataReader reader,string dir,int batch,List<ResultInfo> results,CancellationToken token,Action<string> log) {
   do {
    token.ThrowIfCancellationRequested();if(reader.FieldCount<=0)continue;
    int number=results.Count+1;string stem="结果集_"+number.ToString("D3");
    var r=new ResultInfo{index=number,batch=batch,columnCount=reader.FieldCount,schema=stem+".columns.json",jsonl=stem+".rows.jsonl",csv=stem+".csv",columns=new List<Dictionary<string,object>>()};
    results.Add(r);
    DataTable schema=reader.GetSchemaTable();
    for(int i=0;i<reader.FieldCount;i++) {
     var col=Util.Obj("ordinal",i,"name",reader.GetName(i),"sqlType",reader.GetDataTypeName(i),"dotNetType",reader.GetFieldType(i).FullName);
     if(schema!=null&&i<schema.Rows.Count)foreach(string key in new[]{"AllowDBNull","ColumnSize","NumericPrecision","NumericScale"})if(schema.Columns.Contains(key)&&schema.Rows[i][key]!=DBNull.Value)col[key]=schema.Rows[i][key];
     r.columns.Add(col);
    }
    Util.Save(Path.Combine(dir,r.schema),r.columns);log("  结果集 "+number+"："+reader.FieldCount+" 列，正在导出…");
    try {
     using(var jsonl=new StreamWriter(Path.Combine(dir,r.jsonl),false,Util.Utf8))
     using(var csv=new StreamWriter(Path.Combine(dir,r.csv),false,new UTF8Encoding(true))) {
      csv.WriteLine(string.Join(",",Enumerable.Range(0,reader.FieldCount).Select(i=>Util.Csv(reader.GetName(i),reader.GetName(i)))));
      var raw=new object[reader.FieldCount];
      while(reader.Read()) {
       token.ThrowIfCancellationRequested();var sqlReader=reader as SqlDataReader;
       Providers.ReadValues(reader,raw);
       var values=new string[raw.Length];
       for(int i=0;i<raw.Length;i++)values[i]=Util.Value(raw[i]);
       jsonl.WriteLine(Util.Json(values));csv.WriteLine(string.Join(",",Enumerable.Range(0,raw.Length).Select(i=>Util.Csv(raw[i],values[i]))));r.rowCount++;
       for(int i=0;i<r.columns.Count;i++)if(string.Equals((string)r.columns[i]["name"],"error_number",StringComparison.OrdinalIgnoreCase)&&values[i]!=null)r.reportedError=true;
       if(r.rowCount%10000==0)log("  结果集 "+number+"：已导出 "+r.rowCount+" 行");
      }
     }
     r.complete=true;
    } finally {Util.Save(Path.Combine(dir,stem+".status.json"),r);}
    log("  结果集 "+number+" 完成："+r.rowCount+" 行"+(r.reportedError?"（包含SQL自行返回的错误信息）":""));
   } while(reader.NextResult());
  }
  public string Run(Options opt,string outputRoot,CancellationToken token,Action<string> log) {
   string name=DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6);
   string dir=Path.Combine(outputRoot,name);Directory.CreateDirectory(dir);
   if(opt.Kind==DatabaseKind.MongoDB)return MongoExport.Run(opt,dir,token,log);
   var files=new List<FileInfoResult>();var target=Providers.Target(opt);
   var manifest=Util.Obj("formatVersion",1,"toolVersion",Util.ToolVersion,"startedAt",DateTimeOffset.Now.ToString("o"),"databaseType",opt.Kind.ToString(),"server",target[0],"database",target[1],"commonWriteGuard",opt.Guard,"status","running","selectedFiles",opt.Files.Select(Path.GetFileName).ToArray(),"files",files,"valueEncoding","PostgreSQL uses server text representations to preserve arbitrary numeric precision, arrays, JSON and bytea; other relational drivers use provider-specific exact values. JSONL rows are arrays of strings or null; schema retains SQL types; decimal/bigint are exact strings; binary is base64; CSV may prefix text formulas with apostrophe.");
   string rootStatus="completed";
   try {
    for(int f=0;f<opt.Files.Length;f++) {
     if(token.IsCancellationRequested){rootStatus="cancelled";break;}
     string path=opt.Files[f];var item=new FileInfoResult{name=Path.GetFileName(path),status="running"};files.Add(item);
     string fd=Path.Combine(dir,(f+1).ToString("D2")+"_"+Util.Safe(Path.GetFileNameWithoutExtension(path)));Directory.CreateDirectory(fd);
     var watch=Stopwatch.StartNew();log("["+(f+1)+"/"+opt.Files.Length+"] "+item.name);
     try {
      item.sha256=Util.Hash(path);string sql=Util.ReadSql(path);var batches=Dialect.Split(sql,opt.Kind);item.batchCount=batches.Count;
      if(batches.Count==0)throw new InvalidOperationException("文件没有可执行SQL。");
      if(opt.Guard){string reason=Dialect.Guard(sql,opt.Kind);if(reason!=null)throw new InvalidOperationException(reason);}
      using(var messages=new StreamWriter(Path.Combine(fd,"messages.txt"),false,Util.Utf8))
      using(var conn=Providers.Create(opt)) {
       Providers.Messages(conn,text=>{messages.WriteLine(Redact(text,opt.Password));messages.Flush();});
       conn.OpenAsync(token).GetAwaiter().GetResult();
       for(int b=0;b<batches.Count;b++) {
        token.ThrowIfCancellationRequested();
        using(var cmd=conn.CreateCommand()) {
         cmd.CommandText=batches[b].Text;cmd.CommandTimeout=opt.Timeout;Providers.Configure(cmd);
         lock(CommandLock)ActiveCommand=cmd;
         try {using(var reader=cmd.ExecuteReader())WriteReader(reader,fd,b+1,item.resultSets,token,log);item.completedBatches++;}
         catch(DbException ex){item.sqlErrors.AddRange(Providers.Errors(ex,b+1,batches[b].StartLine,opt.Password));throw;}
         finally {lock(CommandLock)ActiveCommand=null;}
        }
       }
      }
      item.status=item.resultSets.Any(x=>x.reportedError)?"completed_with_reported_errors":"completed";
     } catch(Exception ex) {
      item.status=token.IsCancellationRequested?"cancelled":"failed";item.error=Redact(ex.Message,opt.Password);
      var sx=ex as DbException;
      if(sx!=null && item.sqlErrors.Count==0)item.sqlErrors.AddRange(Providers.Errors(sx,0,0,opt.Password));
      File.WriteAllText(Path.Combine(fd,"error.txt"),Redact(ex.ToString(),opt.Password),Util.Utf8);
      Util.Save(Path.Combine(fd,"sql_errors.json"),item.sqlErrors);log("  "+(item.status=="cancelled"?"已取消":"执行失败")+"："+item.error);
     } finally {
      item.elapsedMs=watch.ElapsedMilliseconds;Util.Save(Path.Combine(fd,"script_summary.json"),item);
      Util.Save(Path.Combine(dir,"manifest.json"),manifest);
     }
     if(item.status=="cancelled"){rootStatus="cancelled";break;}
    }
    if(rootStatus!="cancelled"&&files.Any(x=>x.status!="completed"))rootStatus="completed_with_errors";
   } catch(Exception ex) {rootStatus="failed";manifest["fatalError"]=Redact(ex.ToString(),opt.Password);log("导出中断："+Redact(ex.Message,opt.Password));}
   finally {
    manifest["status"]=rootStatus;manifest["finishedAt"]=DateTimeOffset.Now.ToString("o");manifest["selectedFileCount"]=opt.Files.Length;manifest["processedFileCount"]=files.Count;
    Util.Save(Path.Combine(dir,"manifest.json"),manifest);
    File.WriteAllText(Path.Combine(dir,"结果说明.txt"),"状态："+rootStatus+"\r\n每个脚本有独立目录。每个结果集均保留列信息（columns.json）、完整数据（rows.jsonl）及Excel可打开的CSV；空结果也有列信息和空文件。\r\nJSONL每行是按列顺序排列的数组：NULL为null，其余原值按字符串保留，避免长整数/金额精度丢失。原始换行由JSON转义保存，无截断。二进制为base64。CSV的危险文本公式添加单引号，规范原值以JSONL为准。\r\n请把本次运行的整个ZIP回传，不要只传最后一个CSV。\r\n本工具不保存密码、不复制SQL源文件；数据库错误及SQL返回的数据可能包含业务信息，请按业务资料保管。\r\n脚本中自行捕获的错误结果集如含error_number列会标记reportedError；其他业务错误仍需查看结果。",Util.Utf8);
   }
   string zip=dir+".zip";
   try{ZipFile.CreateFromDirectory(dir,zip,CompressionLevel.Optimal,false);log("已生成结果包："+zip);return zip;}
   catch(Exception ex){log("压缩失败，完整结果仍在："+dir+"；原因："+Redact(ex.Message,opt.Password));return dir;}
  }
 }
 sealed partial class MainForm:Form {
  TextBox host=new TextBox(),port=new TextBox(),db=new TextBox(),user=new TextBox(),password=new TextBox(),logBox=new TextBox();
  CheckBox windows=new CheckBox(),encrypt=new CheckBox(),trust=new CheckBox(),guard=new CheckBox();
  NumericUpDown timeout=new NumericUpDown();ListBox scripts=new ListBox();
  Button test=new AppButton(),run=new AppButton(primary: true),cancel=new AppButton(),open=new AppButton();
  TableLayoutPanel config=new TableLayoutPanel();FlowLayoutPanel fileButtons=new FlowLayoutPanel();
  TextBox outputPath=new TextBox();CheckBox autoOutput=new CheckBox();TableLayoutPanel outputPanel=new TableLayoutPanel();
  ComboBox engine=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
  TextBox service=new TextBox{Dock=DockStyle.Fill};Label databaseHelp=new Label{AutoSize=true};
  DatabaseKind Kind{get{return (DatabaseKind)engine.SelectedIndex;}}
  Label status=new Label(); CancellationTokenSource cancellation;Exporter exporter;string latest;
  public MainForm() {
   Text="数据库查询结果导出工具";Font=new Font("Microsoft YaHei UI",9F);StartPosition=FormStartPosition.CenterScreen;
   ClientSize=new Size(1060,845);MinimumSize=new Size(980,780);BackColor=Color.FromArgb(246,248,251);
   var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=8};
   root.RowStyles.Add(new RowStyle(SizeType.Absolute,57));root.RowStyles.Add(new RowStyle(SizeType.Absolute,240));root.RowStyles.Add(new RowStyle(SizeType.Absolute,44));root.RowStyles.Add(new RowStyle(SizeType.Percent,42));root.RowStyles.Add(new RowStyle(SizeType.Absolute,48));root.RowStyles.Add(new RowStyle(SizeType.Absolute,47));root.RowStyles.Add(new RowStyle(SizeType.Percent,58));root.RowStyles.Add(new RowStyle(SizeType.Absolute,29));Controls.Add(root);
   var title=new Label{Text="数据库查询结果导出\nSQL Server · MySQL · PostgreSQL · Oracle · MongoDB",AutoSize=true,Font=new Font(Font.FontFamily,13F,FontStyle.Bold),ForeColor=Color.FromArgb(26,53,85)};AddHeader(root,title);
   config.Dock=DockStyle.Fill;config.ColumnCount=6;config.RowCount=6;
   for(int i=0;i<3;i++){config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,75));config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33F));}
   for(int i=0;i<6;i++)config.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
   AddField("服务器",host,0,1);AddField("端口",port,2,1);AddField("数据库",db,4,1);
   AddField("用户名",user,0,2);AddField("密码",password,2,2);
   windows.Text="Windows身份验证";windows.AutoSize=true;config.Controls.Add(windows,4,4);config.SetColumnSpan(windows,2);
   port.Text="1433";db.Text="";password.UseSystemPasswordChar=true;
   encrypt.Text="加密连接";encrypt.Checked=true;encrypt.AutoSize=true;trust.Text="信任服务器证书";trust.AutoSize=true;
   config.Controls.Add(encrypt,0,3);config.SetColumnSpan(encrypt,2);config.Controls.Add(trust,2,3);config.SetColumnSpan(trust,2);
   var tp=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};tp.Controls.Add(new Label{Text="查询超时(秒)",AutoSize=true,Padding=new Padding(0,5,0,0)});timeout.Minimum=1;timeout.Maximum=7200;timeout.Value=300;timeout.Width=75;tp.Controls.Add(timeout);config.Controls.Add(tp,4,3);config.SetColumnSpan(tp,2);
   guard.Text="拦截常见写入语句（辅助检查）";guard.Checked=true;guard.AutoSize=true;config.Controls.Add(guard,0,4);config.SetColumnSpan(guard,4);
   config.Controls.Add(new Label{Text="数据库类型",AutoSize=true,Padding=new Padding(0,5,0,0)},0,0);engine.Items.AddRange(Enum.GetNames(typeof(DatabaseKind)));config.Controls.Add(engine,1,0);
   config.Controls.Add(new Label{Text="认证库/SID",AutoSize=true,Padding=new Padding(0,5,0,0)},4,2);config.Controls.Add(service,5,2);
   config.Controls.Add(databaseHelp,0,5);config.SetColumnSpan(databaseHelp,6);
   root.Controls.Add(config);
   fileButtons.Dock=DockStyle.Fill;fileButtons.WrapContents=false;
   AddButton(fileButtons,"添加查询文件…",()=>{using(var f=new OpenFileDialog{Filter="查询脚本 (*.sql;*.mongo.json)|*.sql;*.mongo.json",Multiselect=true,Title="可一次选择多份核查脚本"})if(f.ShowDialog()==DialogResult.OK)AddScripts(f.FileNames);});
   AddButton(fileButtons,"移除所选",()=>{foreach(var o in scripts.SelectedItems.Cast<object>().ToArray())scripts.Items.Remove(o);RefreshDefaultOutput();});
   AddButton(fileButtons,"清空列表",()=>{scripts.Items.Clear();RefreshDefaultOutput();});fileButtons.Controls.Add(new Label{Text="也可将多个 .sql / .mongo.json 文件拖入下方列表",AutoSize=true,Padding=new Padding(8,7,0,0),ForeColor=Color.DimGray});root.Controls.Add(fileButtons);
   scripts.Dock=DockStyle.Fill;scripts.SelectionMode=SelectionMode.MultiExtended;scripts.HorizontalScrollbar=true;scripts.AllowDrop=true;
   scripts.DragEnter+=ScriptsDragEnter;scripts.DragDrop+=ScriptsDragDrop;root.Controls.Add(scripts);
   outputPanel.Dock=DockStyle.Fill;outputPanel.ColumnCount=4;outputPanel.RowCount=1;outputPanel.Padding=new Padding(0,6,0,0);
   outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,70));outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,155));outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
   outputPanel.Controls.Add(new Label{Text="导出目录",AutoSize=true,Padding=new Padding(0,6,0,0)},0,0);
   outputPath.Dock=DockStyle.Fill;outputPath.ReadOnly=true;outputPath.AccessibleName="导出目录";outputPanel.Controls.Add(outputPath,1,0);
   autoOutput.Text="按首个脚本目录";autoOutput.Checked=true;autoOutput.AutoSize=true;autoOutput.Padding=new Padding(5,3,0,0);outputPanel.Controls.Add(autoOutput,2,0);
   var chooseOutput=new AppButton{Text="选择…",Dock=DockStyle.Top,Height=29};outputPanel.Controls.Add(chooseOutput,3,0);
   autoOutput.CheckedChanged+=(s,e)=>{outputPath.ReadOnly=autoOutput.Checked;RefreshDefaultOutput();};
   chooseOutput.Click+=(s,e)=>{using(var f=new FolderBrowserDialog{Description="选择本次结果保存目录",SelectedPath=Directory.Exists(outputPath.Text)?outputPath.Text:AppDomain.CurrentDomain.BaseDirectory})if(f.ShowDialog()==DialogResult.OK){autoOutput.Checked=false;outputPath.Text=f.SelectedPath;}};
   root.Controls.Add(outputPanel);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(0,6,0,0),WrapContents=false};
   SetupButton(test,"测试连接",130);SetupButton(run,"执行并导出",150);SetupButton(cancel,"取消执行",110);SetupButton(open,"打开结果目录",145);
   cancel.Enabled=false;open.Enabled=false;
   actions.Controls.AddRange(new Control[]{test,run,cancel,open});ConfigureLogButtons(actions);root.Controls.Add(actions);
   logBox.Dock=DockStyle.Fill;logBox.Multiline=true;logBox.ReadOnly=true;logBox.ScrollBars=ScrollBars.Both;logBox.WordWrap=false;logBox.BackColor=Color.White;root.Controls.Add(logBox);
   status.Dock=DockStyle.Fill;status.Text="默认保存至首个脚本所在目录（时间戳文件夹及 ZIP）；不保存密码。";status.AutoEllipsis=true;root.Controls.Add(status);
   windows.CheckedChanged+=(s,e)=>{user.Enabled=password.Enabled=!windows.Checked;};
   test.Click+=async(s,e)=>await Test();run.Click+=async(s,e)=>await Run();cancel.Click+=(s,e)=>{if(cancellation!=null)cancellation.Cancel();if(exporter!=null)exporter.Cancel();Log("正在取消，请等待当前命令结束…");};
   open.Click+=(s,e)=>{if(!string.IsNullOrEmpty(latest))Process.Start(new ProcessStartInfo{FileName=Directory.Exists(latest)?latest:Path.GetDirectoryName(latest),UseShellExecute=true});};
   FormClosing+=(s,e)=>{if(cancellation!=null){e.Cancel=true;Log("任务仍在进行，请先取消执行，待结果保存后再关闭窗口。");}};
   InitializeProfiles();
   Shown+=(s,e)=>{var area=Screen.FromControl(this).WorkingArea;MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-20),Math.Min(MinimumSize.Height,area.Height-20));if(Height>area.Height-20)Height=area.Height-20;if(Width>area.Width-20)Width=area.Width-20;CenterToScreen();};
  }
  internal void RefreshDefaultOutput(){if(autoOutput.Checked)outputPath.Text=scripts.Items.Count>0?Path.GetDirectoryName((string)scripts.Items[0]):"";}
  internal static bool IsSqlFile(string path){return !string.IsNullOrWhiteSpace(path)&&File.Exists(path)&&(string.Equals(Path.GetExtension(path),".sql",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".mongo.json",StringComparison.OrdinalIgnoreCase));}
  internal void AddScripts(IEnumerable<string> paths){
   if(cancellation!=null)return;
   int added=0,skipped=0;
   foreach(string path in paths){
    if(!IsSqlFile(path)){skipped++;continue;}
    string full=Path.GetFullPath(path);
    if(scripts.Items.Cast<string>().Any(x=>string.Equals(x,full,StringComparison.OrdinalIgnoreCase)))continue;
    scripts.Items.Add(full);added++;
   }
   RefreshDefaultOutput();
   if(added>0)Log("已添加 "+added+" 份脚本；列表共 "+scripts.Items.Count+" 份。");
   if(skipped>0)Log("已忽略 "+skipped+" 个不支持的文件、文件夹或不存在的路径。");
  }
  internal void ScriptsDragEnter(object sender,DragEventArgs e){
   var paths=e.Data!=null&&e.Data.GetDataPresent(DataFormats.FileDrop)?e.Data.GetData(DataFormats.FileDrop) as string[]:null;
   e.Effect=cancellation==null&&scripts.Enabled&&(e.AllowedEffect&DragDropEffects.Copy)!=0&&paths!=null&&paths.Any(IsSqlFile)?DragDropEffects.Copy:DragDropEffects.None;
  }
  internal void ScriptsDragDrop(object sender,DragEventArgs e){
   if(cancellation!=null||!scripts.Enabled||e.Data==null||!e.Data.GetDataPresent(DataFormats.FileDrop))return;
   var paths=e.Data.GetData(DataFormats.FileDrop) as string[];if(paths!=null)AddScripts(paths);
  }
  internal string[] ScriptFiles{get{return scripts.Items.Cast<string>().ToArray();}}
  [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
  internal string OutputDirectory{get{return outputPath.Text;}set{autoOutput.Checked=false;outputPath.Text=value;}}
  internal void ClearScripts(){scripts.Items.Clear();RefreshDefaultOutput();}
  internal void UseScriptDirectory(){autoOutput.Checked=true;RefreshDefaultOutput();}
  void AddField(string label,TextBox field,int c,int r){config.Controls.Add(new Label{Text=label,AutoSize=true,Padding=new Padding(0,5,0,0)},c,r);field.Dock=DockStyle.Fill;config.Controls.Add(field,c+1,r);}
  void AddButton(Control parent,string text,Action action){var b=new AppButton();SetupButton(b,text,132);b.Click+=(s,e)=>action();parent.Controls.Add(b);}
  void SetupButton(Button b,string text,int width){b.Text=text;b.Width=width;b.Height=31;}
  void Log(string s){if(InvokeRequired){if(!IsDisposed)BeginInvoke((Action)(()=>Log(s)));return;}string line=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"  "+SanitizeLog(s);logBox.AppendText(line+Environment.NewLine);if(!runLog.Append(line)&&!logFailureReported){logFailureReported=true;logBox.AppendText("日志文件暂时无法写入，当前仍显示窗口日志。"+Environment.NewLine);}}
  string Connection() {
   return Providers.Connection(Kind,host.Text,port.Text,db.Text,user.Text,password.Text,windows.Checked,encrypt.Checked,trust.Checked,service.Text);

  }
  void Busy(bool b){config.Enabled=fileButtons.Enabled=scripts.Enabled=outputPanel.Enabled=test.Enabled=run.Enabled=clearLog.Enabled=!b;cancel.Enabled=b;open.Enabled=!b&&!string.IsNullOrEmpty(latest);}
  async Task Test(){string cs,pw;try{cs=Connection();pw=password.Text;}catch(Exception ex){MessageBox.Show(this,ex.Message,"请检查连接信息");return;}
   var testOptions=new Options{Kind=Kind,ConnectionString=cs,Password=pw,Timeout=(int)timeout.Value};Busy(true);cancellation=new CancellationTokenSource();Log("测试连接中…");var exp=new Exporter();
   try{await Task.Run(()=>{Providers.Test(testOptions,cancellation.Token);});Log("连接成功。密码不会保存到磁盘。");}
   catch(Exception ex){Log("连接失败："+exp.Redact(ex.Message,pw));}finally{cancellation.Dispose();cancellation=null;Busy(false);}
  }
  async Task Run(){Options opt;try{opt=new Options{Kind=Kind,ConnectionString=Connection(),Password=password.Text,Files=scripts.Items.Cast<string>().ToArray(),Timeout=(int)timeout.Value,Guard=guard.Checked};if(opt.Files.Length==0)throw new InvalidOperationException("请先添加至少一份查询脚本。");foreach(var file in opt.Files)if((opt.Kind==DatabaseKind.MongoDB)!=file.EndsWith(".mongo.json",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("MongoDB 使用 .mongo.json，关系型数据库使用 .sql；请分别执行。");}
   catch(Exception ex){MessageBox.Show(this,ex.Message,"请检查输入");return;}
   string output;
   try{RefreshDefaultOutput();if(string.IsNullOrWhiteSpace(outputPath.Text))throw new InvalidOperationException("请选择导出目录。");output=Path.GetFullPath(outputPath.Text.Trim());}
   catch(Exception ex){MessageBox.Show(this,ex.Message,"请检查导出目录");return;}
   Busy(true);cancellation=new CancellationTokenSource();exporter=new Exporter();latest=null;
   try{latest=await Task.Run(()=>exporter.Run(opt,output,cancellation.Token,Log));status.Text="结果已保存："+latest;Log("导出结束。请回传本次运行的整个 ZIP；有错误时也会保留结果。");}
   catch(Exception ex){Log("未完成导出："+exporter.Redact(ex.Message,opt.Password));status.Text="导出未完成，请查看日志。请确认导出目录可写，或点击“选择…”更换目录。";}
   finally{opt.ConnectionString=null;opt.Password=null;exporter=null;cancellation.Dispose();cancellation=null;Busy(false);}
  }
 }
 static class SelfTest {
  static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);}
  public static void Run(string folder) {
   Directory.CreateDirectory(folder);
   var bs=SqlText.Split("SELECT 'a\nGO\nb';\n/* x\nGO\n/* nested */ */\nGO\nSELECT [GO],\"GO\";\nGO 2 -- repeat\n");Check(bs.Count==3,"GO ignores strings/comments and supports repeats");Check(bs[1].StartLine==8,"GO original line numbers");
   Check(SqlText.Guard("-- DELETE\nSELECT 1;")==null,"comments do not trip guard");Check(SqlText.Guard("EXEC sys.sp_executesql N'SELECT 1'")==null,"dynamic select allowed");Check(SqlText.Guard("EXEC sys.sp_executesql N'DELETE FROM x'")!=null,"dynamic write flagged");
   Check(SqlText.Guard("SELECT 1 INTO dbo.x")!=null,"SELECT INTO flagged");Check(SqlText.Guard("EXEC xp_cmdshell 'x'")!=null,"other EXEC flagged");
   Check(Util.Value(SqlDecimal.Parse("12345678901234567890.123456789012345678"))=="12345678901234567890.123456789012345678","SQL decimal38 without CLR decimal overflow");
   var ds=new DataSet();var t=new DataTable("rows");t.Columns.Add("section",typeof(string));t.Columns.Add("text",typeof(string));t.Columns.Add("amount",typeof(decimal));t.Columns.Add("big_id",typeof(long));t.Columns.Add("empty",typeof(string));t.Columns.Add("formula",typeof(string));
   t.Rows.Add("A01","中文\r\n逗号,\"引号\"\\末尾"+new string('长',70000),1234567890123456.78m,9007199254740993L,DBNull.Value,"=1+1");ds.Tables.Add(t);
   var empty=new DataTable("empty");empty.Columns.Add("error_number",typeof(int));empty.Columns.Add("error_message",typeof(string));ds.Tables.Add(empty);
   var error=new DataTable("reported");error.Columns.Add("error_number",typeof(int));error.Columns.Add("error_message",typeof(string));error.Rows.Add(207,"字段不存在");ds.Tables.Add(error);
   var sets=new List<ResultInfo>();using(var reader=ds.CreateDataReader())Exporter.WriteReader(reader,folder,1,sets,CancellationToken.None,s=>{});
   Check(sets.Count==3,"all results including empty");Check(sets[1].rowCount==0&&sets[1].complete,"empty metadata");Check(sets[2].reportedError,"SQL-caught error flag");
   string json=File.ReadAllText(Path.Combine(folder,sets[0].jsonl),Util.Utf8);var row=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<string[]>(json.Trim());
   Check(row[1]==(string)t.Rows[0][1],"long Unicode multiline exact roundtrip");Check(row[2]=="1234567890123456.78"&&row[3]=="9007199254740993","decimal and bigint precision");Check(row[4]==null,"NULL distinct from empty");Check(row[5]=="=1+1","canonical formula text exact");Check(File.ReadAllText(Path.Combine(folder,sets[0].csv)).Contains("\"'=1+1\""),"CSV formula prefix");
   byte[] bom=File.ReadAllBytes(Path.Combine(folder,sets[0].csv));Check(bom[0]==239&&bom[1]==187&&bom[2]==191,"Excel UTF8 BOM");
   var many=new DataTable();many.Columns.Add("n",typeof(int));for(int i=0;i<10010;i++)many.Rows.Add(i);
   var cancelMid=new CancellationTokenSource();var partial=new List<ResultInfo>();string pd=Path.Combine(folder,"partial");Directory.CreateDirectory(pd);
   try{using(var rr=many.CreateDataReader())Exporter.WriteReader(rr,pd,1,partial,cancelMid.Token,s=>{if(s.Contains("已导出"))cancelMid.Cancel();});throw new Exception("cancel expected");}catch(OperationCanceledException){}
   Check(partial.Count==1 && partial[0].rowCount==10000 && !partial[0].complete,"mid-stream cancellation retains partial data and incomplete flag");
   var cs=new SqlConnectionStringBuilder{DataSource="tcp:invalid.example,1433",InitialCatalog="ExampleDb",UserID="x",Password="secret;!测试",ConnectTimeout=1,Pooling=false}.ConnectionString;
   string sql=Path.Combine(folder,"failure.sql");File.WriteAllText(sql,"SELECT 1",Util.Utf8);
   var exp=new Exporter();string zip=exp.Run(new Options{ConnectionString=cs,Password="secret;!测试",Timeout=1,Files=new[]{sql},Guard=true},Path.Combine(folder,"failure-export"),CancellationToken.None,s=>{});
   Check(File.Exists(zip),"failed connection still produces ZIP");
   using(var archive=ZipFile.OpenRead(zip)){var entry=archive.GetEntry("manifest.json");using(var sr=new StreamReader(entry.Open())){string manifest=sr.ReadToEnd();Check(!manifest.Contains("secret;!测试"),"no password in manifest");Check(manifest.Contains("completed_with_errors"),"failed connection status");}}
   string encoding=Path.Combine(folder,"gbk.sql");File.WriteAllText(encoding,"SELECT N'中文';",Encoding.GetEncoding(936));Check(Util.ReadSql(encoding)=="SELECT N'中文';","GBK reading");
   string cancelled=exp.Run(new Options{ConnectionString=cs,Password="secret;!测试",Timeout=1,Files=new[]{sql},Guard=true},Path.Combine(folder,"cancel-export"),new CancellationToken(true),s=>{});Check(File.Exists(cancelled),"cancelled run has package");
   File.WriteAllText(Path.Combine(folder,"selftest.ok.txt"),"PASS: GO/line numbers; static write guard; 3 result sets; empty set; reported error; 70000-char Unicode multiline; decimal/bigint/null; CSV; GBK; failed connection ZIP; cancellation package; mid-stream cancellation with 10000 rows retained; password exclusion.\r\nNo live SQL Server success-path test performed.",Util.Utf8);
  }
 }
 static class Program {
  [STAThread]static int Main(string[] args) {
   Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);CultureInfo.DefaultThreadCurrentCulture=CultureInfo.InvariantCulture;
   if(args.Length>=4&&args[0]=="--check-script"){try{DatabaseKind kind=(DatabaseKind)Enum.Parse(typeof(DatabaseKind),args[1],true);var rows=new List<object>();foreach(string file in args.Skip(3)){string text=Util.ReadSql(file);if(kind==DatabaseKind.MongoDB){MongoExport.Query(text);rows.Add(Util.Obj("file",Path.GetFileName(file),"valid",true));}else{var batches=Dialect.Split(text,kind);string reason=Dialect.Guard(text,kind);rows.Add(Util.Obj("file",Path.GetFileName(file),"batches",batches.Count,"guardReason",reason));if(reason!=null)throw new Exception(Path.GetFileName(file)+": "+reason);}}Util.Save(args[2],rows);return 0;}catch(Exception ex){File.WriteAllText(args[2],ex.ToString());return 1;}}
   if(args.Length>=2&&args[0]=="--selftest"){try{SelfTest.Run(args[1]);return 0;}catch(Exception ex){Directory.CreateDirectory(args[1]);File.WriteAllText(Path.Combine(args[1],"selftest.failed.txt"),ex.ToString());return 1;}}
   if(args.Length>=3&&args[0]=="--check-sql"){try{var rows=new List<object>();foreach(string path in args.Skip(2)){string s=Util.ReadSql(path);var batches=SqlText.Split(s);string reason=SqlText.Guard(s);rows.Add(Util.Obj("file",Path.GetFileName(path),"batches",batches.Count,"guardReason",reason));if(reason!=null)throw new Exception(Path.GetFileName(path)+": "+reason);}Util.Save(args[1],rows);return 0;}catch(Exception ex){File.WriteAllText(args[1],ex.ToString());return 1;}}
   Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new MainForm());return 0;
  }
 }
}
