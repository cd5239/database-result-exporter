using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlResultExporter {
 static class Dialect {
  public static string Guard(string text,DatabaseKind kind){
   if(kind==DatabaseKind.SqlServer)return SqlText.Guard(text);
   // Deliberately conservative: this is an auxiliary check and may flag literal text.
   var m=Regex.Match(text,@"\b(INSERT|UPDATE|DELETE|MERGE|REPLACE|CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE|DENY|CALL|EXEC|EXECUTE|COPY|LOAD|OUTFILE|DUMPFILE|INTO|VACUUM|DO)\b",RegexOptions.IgnoreCase);
   return m.Success?"辅助检查检测到 "+m.Value+"，请核对脚本；数据库权限决定实际写入能力。":null;
  }
  public static List<Batch> Split(string text,DatabaseKind kind){
   if(kind==DatabaseKind.SqlServer)return SqlText.Split(text);
   if(kind==DatabaseKind.MongoDB)throw new InvalidOperationException("MongoDB 使用 .mongo.json 查询定义。");
   if(kind==DatabaseKind.PostgreSQL&&Regex.IsMatch(text,@"(?i)\bE'"))throw new InvalidOperationException("当前不支持 PostgreSQL E'...' 转义字符串，请使用美元引号。");
   var result=new List<Batch>();var buffer=new StringBuilder();int state=0,depth=0,line=1,start=1;string dollar=null;
   // Oracle PL/SQL blocks use the SQL*Plus standalone slash; reject SQL*Plus commands.
   if(kind==DatabaseKind.Oracle)return Oracle(text);
   for(int i=0;i<text.Length;i++){
    char c=text[i],n=i+1<text.Length?text[i+1]:'\0';
    if(c=='\n')line++;
    if(dollar!=null){if(text.Substring(i).StartsWith(dollar,StringComparison.Ordinal)){buffer.Append(dollar);i+=dollar.Length-1;dollar=null;}else buffer.Append(c);continue;}
    if(state==4){buffer.Append(c);if(c=='/'&&n=='*'){depth++;buffer.Append(n);i++;}else if(c=='*'&&n=='/'){buffer.Append(n);i++;if(--depth==0)state=0;}continue;}
    if(state==5){buffer.Append(c);if(c=='\n')state=0;continue;}
    if(state>=1&&state<=3){buffer.Append(c);char quote=state==1?'\'':state==2?'"':'`';if(c=='\\'&&kind==DatabaseKind.MySQL&&i+1<text.Length){buffer.Append(n);i++;continue;}if(c==quote){if(n==quote){buffer.Append(n);i++;}else state=0;}continue;}
    if(c=='-'&&n=='-' || kind==DatabaseKind.MySQL&&c=='#'){state=5;buffer.Append(c);continue;}
    if(c=='/'&&n=='*'){state=4;depth=1;buffer.Append("/*");i++;continue;}
    if(c=='\''||c=='"'||c=='`'){state=c=='\''?1:c=='"'?2:3;buffer.Append(c);continue;}
    if(c=='$'&&kind==DatabaseKind.PostgreSQL){var match=Regex.Match(text.Substring(i),@"^\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$");if(match.Success){dollar=match.Value;buffer.Append(dollar);i+=dollar.Length-1;continue;}}
    if(c==';'){Add(result,buffer,start);start=line;continue;}buffer.Append(c);
   }
   if(state!=0&&state!=5||dollar!=null)throw new InvalidOperationException("脚本中存在未闭合的字符串或注释。");
   Add(result,buffer,start);
   if(Regex.IsMatch(text,@"(?im)^\s*(DELIMITER\b|\\|SOURCE\b|\!|:)") )throw new InvalidOperationException("不支持数据库客户端指令（如 DELIMITER、SOURCE、psql 元命令），请提供普通 SQL。");
   return result;
  }
  static void Add(List<Batch> result,StringBuilder buffer,int line){string value=buffer.ToString();buffer.Clear();if(value.Trim().Length>0)result.Add(new Batch{Text=value,StartLine=line});}
  static List<Batch> Oracle(string text){
   if(Regex.IsMatch(text,@"(?i)\bq'"))throw new InvalidOperationException("当前不支持 Oracle q'...' 替代引号，请使用标准单引号。");
   // SQL SELECTs separated by semicolons; PL/SQL block must occupy its own / section.
   if(Regex.IsMatch(text,@"(?im)^\s*(SET\s+|SPOOL\b|PROMPT\b|WHENEVER\b|@|CONNECT\b|EXIT\b)"))throw new InvalidOperationException("不支持 SQL*Plus 客户端指令。");
   var result=new List<Batch>();var section=new StringBuilder();int line=0,start=1;
   int state=0,depth=0;
   using(var r=new StringReader(text)){string current;while((current=r.ReadLine())!=null){line++;if(state==0&&current.Trim()=="/"){OracleSection(result,section.ToString(),start);section.Clear();start=line+1;}else{section.AppendLine(current);ScanOracleLine(current,ref state,ref depth);}}}
   if(state!=0)throw new InvalidOperationException("Oracle 脚本中存在未闭合的字符串或注释。");
   OracleSection(result,section.ToString(),start);return result;
  }
  static void OracleSection(List<Batch> result,string text,int start){
   string lead=Regex.Replace(text,@"(?s)/\*.*?\*/|(?m)--[^\r\n]*","").TrimStart();if(lead.Length==0)return;
   if(Regex.IsMatch(lead,@"^(DECLARE|BEGIN|CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE))\b",RegexOptions.IgnoreCase)){result.Add(new Batch{Text=text.Trim(),StartLine=start});return;}
   // Reuse quote-aware SQL splitting; Oracle alternative quoting is explicitly rejected.
   if(Regex.IsMatch(text,@"(?i)\bq'"))throw new InvalidOperationException("此版本不支持 Oracle q'...' 替代引号，请改用标准单引号。");
   foreach(var batch in Split(text,DatabaseKind.PostgreSQL)){batch.StartLine+=start-1;result.Add(batch);}
  }
  static void ScanOracleLine(string line,ref int state,ref int depth){for(int i=0;i<line.Length;i++){char c=line[i],n=i+1<line.Length?line[i+1]:'\0';if(state==3){if(c=='/'&&n=='*'){depth++;i++;}else if(c=='*'&&n=='/'){if(--depth==0)state=0;i++;}continue;}if(state==1||state==2){char quote=state==1?'\'':'"';if(c==quote){if(n==quote)i++;else state=0;}continue;}if(c=='-'&&n=='-')break;if(c=='/'&&n=='*'){state=3;depth=1;i++;}else if(c=='\'')state=1;else if(c=='"')state=2;}}
 }
}
