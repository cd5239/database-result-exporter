// Retain the v1 exporter call sites with the platform JSON serializer.
namespace System.Web.Script.Serialization {
 public sealed class JavaScriptSerializer {
  public int MaxJsonLength { get; set; }
  public int RecursionLimit { get; set; }
  static readonly System.Text.Json.JsonSerializerOptions Options = new System.Text.Json.JsonSerializerOptions { IncludeFields=true };
  public string Serialize(object value) { return System.Text.Json.JsonSerializer.Serialize(value,Options); }
  public T Deserialize<T>(string value) { return System.Text.Json.JsonSerializer.Deserialize<T>(value,Options); }
 }
}
