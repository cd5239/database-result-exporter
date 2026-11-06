using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace SqlResultExporter {
 static class MongoExport {
  static readonly JsonWriterSettings Canonical=new JsonWriterSettings{OutputMode=JsonOutputMode.CanonicalExtendedJson};
  public static string Connection(string host,int port,string db,string user,string password,bool encrypt,bool trust,string authDb){
   if(!System.Text.RegularExpressions.Regex.IsMatch(host,@"^[\w.:-]+$"))throw new InvalidOperationException("MongoDB 服务器仅填写主机名/IP。");
   string authority=host.Contains(":")?"["+host+"]":host;
   string credential=string.IsNullOrEmpty(user)?"":Uri.EscapeDataString(user)+":"+Uri.EscapeDataString(password)+"@";
   return "mongodb://"+credential+authority+":"+port+"/"+Uri.EscapeDataString(db)+"?authSource="+Uri.EscapeDataString(string.IsNullOrEmpty(authDb)?"admin":authDb)+"&tls="+encrypt.ToString().ToLowerInvariant()+"&tlsAllowInvalidCertificates="+trust.ToString().ToLowerInvariant()+"&serverSelectionTimeoutMS=15000&connectTimeoutMS=15000";
  }
  static MongoClient Client(Options opt){var settings=MongoClientSettings.FromConnectionString(opt.ConnectionString);settings.ApplicationName="DatabaseResultExporter";settings.RetryWrites=false;return new MongoClient(settings);}
  public static void Test(Options opt,CancellationToken token){var url=new MongoUrl(opt.ConnectionString);using(var client=Client(opt))client.GetDatabase(url.DatabaseName).RunCommand<BsonDocument>(new BsonDocument("ping",1),cancellationToken:token);}
  internal static BsonDocument Query(string text){
   var q=BsonDocument.Parse(text);var allowed=new HashSet<string>{"collection","operation","filter","projection","sort","limit","pipeline"};
   foreach(var key in q.Names)if(!allowed.Contains(key))throw new InvalidOperationException("不支持的 MongoDB 查询字段："+key);
   if(!q.Contains("collection")||!q["collection"].IsString||string.IsNullOrWhiteSpace(q["collection"].AsString))throw new InvalidOperationException("必须填写 collection。");
   string op=q.GetValue("operation","find").AsString;if(op!="find"&&op!="aggregate")throw new InvalidOperationException("仅支持 find 与 aggregate。");
   if(op=="find"&&q.Contains("pipeline"))throw new InvalidOperationException("find 不接受 pipeline，请使用 aggregate。");
   if(q.Contains("limit")&&q["limit"].ToInt64()<0)throw new InvalidOperationException("limit 不得为负数。");
   if(op=="aggregate"){
    if(q.Contains("filter")||q.Contains("projection")||q.Contains("sort")||q.Contains("limit"))throw new InvalidOperationException("aggregate 的筛选、投影、排序和条数限制请写入 pipeline。");
    if(!q.Contains("pipeline")||!q["pipeline"].IsBsonArray)throw new InvalidOperationException("aggregate 必须填写 pipeline 数组。");
    RejectWriteStages(q["pipeline"]);
   }
   return q;
  }
  static void RejectWriteStages(BsonValue value){if(value.IsBsonDocument){foreach(var e in value.AsBsonDocument){if(e.Name=="$out"||e.Name=="$merge")throw new InvalidOperationException("MongoDB 导出不支持 $out / $merge 写入阶段。");RejectWriteStages(e.Value);}}else if(value.IsBsonArray)foreach(var child in value.AsBsonArray)RejectWriteStages(child);}
  internal static string Json(BsonDocument doc){return doc.ToJson(Canonical);}
  internal static void WriteDocuments(IEnumerable<BsonDocument> documents,string fd,List<ResultInfo> results,CancellationToken token,Action<string> log){
   var r=new ResultInfo{index=1,batch=1,schema="结果集_001.columns.json",jsonl="结果集_001.documents.jsonl",csv="结果集_001.csv",columns=new List<Dictionary<string,object>>()};results.Add(r);
   var keys=new List<string>();var types=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
   try{using(var writer=new StreamWriter(Path.Combine(fd,r.jsonl),false,Util.Utf8))foreach(var doc in documents){token.ThrowIfCancellationRequested();writer.WriteLine(Json(doc));r.rowCount++;foreach(var e in doc){if(!types.ContainsKey(e.Name)){keys.Add(e.Name);types[e.Name]=new HashSet<string>();}types[e.Name].Add(e.Value.BsonType.ToString());}if(r.rowCount%10000==0)log("  已导出 "+r.rowCount+" 份文档");}r.complete=true;}
   finally{
    r.columnCount=keys.Count;for(int i=0;i<keys.Count;i++)r.columns.Add(Util.Obj("ordinal",i,"name",keys[i],"bsonTypes",types[keys[i]].OrderBy(x=>x).ToArray()));
    Util.Save(Path.Combine(fd,r.schema),r.columns);Util.Save(Path.Combine(fd,"结果集_001.status.json"),r);
    // Second pass keeps memory bounded and preserves the union of all top-level fields.
    using(var csv=new StreamWriter(Path.Combine(fd,r.csv),false,new System.Text.UTF8Encoding(true))){csv.WriteLine(string.Join(",",keys.Select(k=>Util.Csv(k,k))));if(File.Exists(Path.Combine(fd,r.jsonl)))foreach(string line in File.ReadLines(Path.Combine(fd,r.jsonl),Util.Utf8)){var doc=BsonDocument.Parse(line);csv.WriteLine(string.Join(",",keys.Select(k=>{BsonValue v;if(!doc.TryGetValue(k,out v)||v.IsBsonNull)return "";string text=v.IsString?v.AsString:v.ToJson(Canonical);return Util.Csv(text,text);})));}}
   }
  }
  static IEnumerable<BsonDocument> Documents(IMongoCollection<BsonDocument> collection,BsonDocument q,int timeout,CancellationToken token){
   TimeSpan duration=TimeSpan.FromSeconds(timeout);IAsyncCursor<BsonDocument> cursor;
   if(q.GetValue("operation","find").AsString=="aggregate"){
    var stages=q["pipeline"].AsBsonArray.Select(v=>v.AsBsonDocument).ToArray();cursor=collection.Aggregate<BsonDocument>(stages,new AggregateOptions{MaxTime=duration,AllowDiskUse=false},token);
   }else{
    var options=new FindOptions<BsonDocument>{MaxTime=duration,BatchSize=1000};
    if(q.Contains("projection"))options.Projection=q["projection"].AsBsonDocument;if(q.Contains("sort"))options.Sort=q["sort"].AsBsonDocument;
    if(q.Contains("limit")&&q["limit"].ToInt64()>0)options.Limit=checked((int)q["limit"].ToInt64());
    cursor=collection.FindSync<BsonDocument>(q.GetValue("filter",new BsonDocument()).AsBsonDocument,options,token);
   }
   using(cursor)while(cursor.MoveNext(token))foreach(var doc in cursor.Current){token.ThrowIfCancellationRequested();yield return doc;}
  }
  public static string Run(Options opt,string dir,CancellationToken token,Action<string> log){
   var url=new MongoUrl(opt.ConnectionString);var files=new List<FileInfoResult>();var manifest=Util.Obj("formatVersion",2,"toolVersion",Util.ToolVersion,"databaseType","MongoDB","server",url.Server.ToString(),"database",url.DatabaseName,"startedAt",DateTimeOffset.Now.ToString("o"),"selectedFiles",opt.Files.Select(Path.GetFileName).ToArray(),"selectedFileCount",opt.Files.Length,"files",files,"valueEncoding","Canonical MongoDB Extended JSON documents; CSV top-level union, nested values use Extended JSON; missing/null CSV fields are empty.");string status="completed";
   try{foreach(string path in opt.Files){token.ThrowIfCancellationRequested();var item=new FileInfoResult{name=Path.GetFileName(path),status="running"};files.Add(item);var timer=Stopwatch.StartNew();string fd=Path.Combine(dir,files.Count.ToString("D2")+"_"+Util.Safe(Path.GetFileNameWithoutExtension(path)));Directory.CreateDirectory(fd);
    try{item.sha256=Util.Hash(path);var query=Query(Util.ReadSql(path));item.batchCount=1;log("["+files.Count+"/"+opt.Files.Length+"] "+item.name);using(var client=Client(opt)){var collection=client.GetDatabase(url.DatabaseName).GetCollection<BsonDocument>(query["collection"].AsString);WriteDocuments(Documents(collection,query,opt.Timeout,token),fd,item.resultSets,token,log);}item.completedBatches=1;item.status="completed";}
    catch(Exception ex){item.status=token.IsCancellationRequested?"cancelled":"failed";item.error=new Exporter().Redact(ex.Message,opt.Password);File.WriteAllText(Path.Combine(fd,"error.txt"),new Exporter().Redact(ex.ToString(),opt.Password),Util.Utf8);log(item.status+"："+item.error);}
    finally{item.elapsedMs=timer.ElapsedMilliseconds;Util.Save(Path.Combine(fd,"script_summary.json"),item);Util.Save(Path.Combine(dir,"manifest.json"),manifest);}
    if(token.IsCancellationRequested){status="cancelled";break;}
   }if(status!="cancelled"&&files.Any(f=>f.status!="completed"))status="completed_with_errors";}
   catch(OperationCanceledException){status="cancelled";}catch(Exception ex){status="failed";manifest["fatalError"]=new Exporter().Redact(ex.Message,opt.Password);}
   finally{manifest["status"]=status;manifest["finishedAt"]=DateTimeOffset.Now.ToString("o");manifest["processedFileCount"]=files.Count;Util.Save(Path.Combine(dir,"manifest.json"),manifest);File.WriteAllText(Path.Combine(dir,"结果说明.txt"),"MongoDB 原始数据为 Canonical Extended JSON，每行一份文档，保留嵌套结构、Decimal128、ObjectId 等类型。CSV 为所有文档顶层字段并集；缺失字段与 null 在 CSV 中均为空，准确原值以 documents.jsonl 为准。请回传整个 ZIP。",Util.Utf8);}
   try{string zip=dir+".zip";ZipFile.CreateFromDirectory(dir,zip,CompressionLevel.Optimal,false);log("已生成结果包："+zip);return zip;}catch(Exception ex){log("压缩失败，结果保留在："+dir+"；"+new Exporter().Redact(ex.Message,opt.Password));return dir;}
  }
 }
}
