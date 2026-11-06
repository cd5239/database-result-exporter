using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace SqlResultExporter {
 enum DatabaseKind { SqlServer, MySQL, PostgreSQL, Oracle, MongoDB }
 static class Providers {
  public static string Connection(DatabaseKind kind,string host,string port,string database,string user,string password,bool windows,bool encrypt,bool trust,string service) {
   if(string.IsNullOrWhiteSpace(host))throw new InvalidOperationException("请填写服务器地址。");
   int p;if(!int.TryParse(port,out p)||p<1||p>65535)throw new InvalidOperationException("端口须为1到65535。");
   if(kind!=DatabaseKind.Oracle && string.IsNullOrWhiteSpace(database))throw new InvalidOperationException("请填写数据库名称。");
   if(kind!=DatabaseKind.MongoDB && !windows && string.IsNullOrWhiteSpace(user))throw new InvalidOperationException("请填写用户名。");
   switch(kind){
    case DatabaseKind.SqlServer:return new SqlConnectionStringBuilder{DataSource="tcp:"+host.Trim()+","+p,InitialCatalog=database.Trim(),IntegratedSecurity=windows,UserID=windows?"":user.Trim(),Password=windows?"":password,ConnectTimeout=15,Encrypt=encrypt,TrustServerCertificate=trust,ApplicationName="DatabaseResultExporter",Pooling=false,PersistSecurityInfo=false}.ConnectionString;
    case DatabaseKind.MySQL:return new MySqlConnectionStringBuilder{Server=host.Trim(),Port=(uint)p,Database=database.Trim(),UserID=user.Trim(),Password=password,ConnectionTimeout=15,Pooling=false,SslMode=encrypt?(trust?MySqlSslMode.Required:MySqlSslMode.VerifyFull):MySqlSslMode.Disabled,AllowUserVariables=true,AllowZeroDateTime=true}.ConnectionString;
    case DatabaseKind.PostgreSQL:return new NpgsqlConnectionStringBuilder{Host=host.Trim(),Port=p,Database=database.Trim(),Username=user.Trim(),Password=password,Timeout=15,Pooling=false,SslMode=encrypt?(trust?SslMode.Require:SslMode.VerifyFull):SslMode.Disable,ApplicationName="DatabaseResultExporter"}.ConnectionString;
    case DatabaseKind.Oracle:
     // Construct descriptors only from simple host/service tokens: no raw descriptor injection.
     if(!System.Text.RegularExpressions.Regex.IsMatch(host.Trim(),@"^[\w.:-]+$"))throw new InvalidOperationException("Oracle 服务器仅填写主机名/IP。");
     string serviceName=string.IsNullOrWhiteSpace(service)?database.Trim():service.Trim();
     if(!System.Text.RegularExpressions.Regex.IsMatch(serviceName,@"^[\w.$-]+$"))throw new InvalidOperationException("请填写有效的 Oracle Service Name 或 SID。");
     if(encrypt && trust)throw new InvalidOperationException("Oracle TCPS 不支持本工具的跳过证书校验选项，请取消“信任服务器证书”。");
     return new OracleConnectionStringBuilder{DataSource="(DESCRIPTION=(CONNECT_TIMEOUT=15)(TRANSPORT_CONNECT_TIMEOUT=15)(ADDRESS=(PROTOCOL="+(encrypt?"TCPS":"TCP")+")(HOST="+host.Trim()+")(PORT="+p+"))(CONNECT_DATA=("+(string.IsNullOrWhiteSpace(service)?"SERVICE_NAME":"SID")+"="+serviceName+")))",UserID=user.Trim(),Password=password,Pooling=false}.ConnectionString;
    case DatabaseKind.MongoDB:return MongoExport.Connection(host.Trim(),p,database.Trim(),user.Trim(),password,encrypt,trust,service.Trim());
    default:throw new NotSupportedException();
   }
  }
  public static DbConnection Create(Options opt){switch(opt.Kind){case DatabaseKind.SqlServer:return new SqlConnection(opt.ConnectionString);case DatabaseKind.MySQL:return new MySqlConnection(opt.ConnectionString);case DatabaseKind.PostgreSQL:return new NpgsqlConnection(opt.ConnectionString);case DatabaseKind.Oracle:return new OracleConnection(opt.ConnectionString);default:throw new NotSupportedException();}}
  public static string[] Target(Options opt){switch(opt.Kind){case DatabaseKind.SqlServer:var s=new SqlConnectionStringBuilder(opt.ConnectionString);return new[]{s.DataSource,s.InitialCatalog};case DatabaseKind.MySQL:var m=new MySqlConnectionStringBuilder(opt.ConnectionString);return new[]{m.Server,m.Database};case DatabaseKind.PostgreSQL:var p=new NpgsqlConnectionStringBuilder(opt.ConnectionString);return new[]{p.Host,p.Database};case DatabaseKind.Oracle:var o=new OracleConnectionStringBuilder(opt.ConnectionString);var host=System.Text.RegularExpressions.Regex.Match(o.DataSource,@"\(HOST=([^)]*)\)").Groups[1].Value;var database=System.Text.RegularExpressions.Regex.Match(o.DataSource,@"\((?:SERVICE_NAME|SID)=([^)]*)\)").Groups[1].Value;return new[]{host,database};default:return new[]{"MongoDB",""};}}
  public static void Test(Options opt,CancellationToken token){if(opt.Kind==DatabaseKind.MongoDB){MongoExport.Test(opt,token);return;}using(var connection=Create(opt))connection.OpenAsync(token).GetAwaiter().GetResult();}
  public static void Configure(DbCommand cmd){var pg=cmd as NpgsqlCommand;if(pg!=null)pg.AllResultTypesAreUnknown=true;var oracle=cmd as OracleCommand;if(oracle!=null)oracle.BindByName=true;}
  public static void Messages(DbConnection connection,Action<string> log){var sql=connection as SqlConnection;if(sql!=null)sql.InfoMessage+=(s,e)=>log(e.Message);var pg=connection as NpgsqlConnection;if(pg!=null)pg.Notice+=(s,e)=>log(e.Notice.MessageText);var my=connection as MySqlConnection;if(my!=null)my.InfoMessage+=(s,e)=>{foreach(var error in e.Errors)log(error.Message);};}
  public static void ReadValues(DbDataReader reader,object[] raw){
   var sql=reader as SqlDataReader;if(sql!=null){sql.GetSqlValues(raw);return;}
   for(int i=0;i<raw.Length;i++){
    if(reader.IsDBNull(i)){raw[i]=DBNull.Value;continue;}
    var my=reader as MySqlDataReader;
    if(my!=null){string type=my.GetDataTypeName(i);if(type.Equals("DECIMAL",StringComparison.OrdinalIgnoreCase)||type.Equals("NEWDECIMAL",StringComparison.OrdinalIgnoreCase)){raw[i]=my.GetMySqlDecimal(i).ToString();continue;}}
    var oracle=reader as OracleDataReader;
    if(oracle!=null){string type=oracle.GetDataTypeName(i).ToUpperInvariant();
     if(type=="DECIMAL"||type=="NUMBER"){raw[i]=oracle.GetOracleDecimal(i).ToString();continue;}
     if(type.Contains("CLOB")){using(var value=oracle.GetOracleClob(i))raw[i]=value.Value;continue;}
     if(type=="BLOB"){using(var value=oracle.GetOracleBlob(i))raw[i]=value.Value;continue;}
     if(type.Contains("TIMESTAMP")){raw[i]=oracle.GetProviderSpecificValue(i).ToString();continue;}
    }
    var valueObject=reader.GetValue(i);if(valueObject is MySqlDateTime){var date=(MySqlDateTime)valueObject;raw[i]=string.Format(CultureInfo.InvariantCulture,"{0:D4}-{1:D2}-{2:D2}T{3:D2}:{4:D2}:{5:D2}.{6:D6}",date.Year,date.Month,date.Day,date.Hour,date.Minute,date.Second,date.Microsecond);}else raw[i]=valueObject;
   }
  }
  public static List<Dictionary<string,object>> Errors(DbException ex,int batch,int start,string password){
   Func<string,string> redact=s=>new Exporter().Redact(s,password);var rows=new List<Dictionary<string,object>>();
   var sql=ex as SqlException;if(sql!=null){foreach(SqlError e in sql.Errors)rows.Add(Util.Obj("number",e.Number,"state",e.State,"severity",e.Class,"procedure",e.Procedure,"batch",batch,"batchLine",e.LineNumber,"fileLine",e.LineNumber>0&&start>0?start+e.LineNumber-1:0,"message",redact(e.Message)));return rows;}
   var pg=ex as PostgresException;var my=ex as MySqlException;var oracle=ex as OracleException;
   rows.Add(Util.Obj("number",my!=null?my.Number:oracle!=null?oracle.Number:ex.ErrorCode,"sqlState",pg!=null?pg.SqlState:ex.SqlState,"batch",batch,"batchStartLine",start,"position",pg!=null?pg.Position:0,"message",redact(ex.Message)));return rows;
  }
 }
}
