// Headless adapters only. No Unity object or native FFI method is executed.
using System;
namespace UnityEngine {
 public class Object { public static T FindFirstObjectByType<T>() where T:new()=>throw new NotSupportedException(); }
 public class ScriptableObject:Object {}
 public class MonoBehaviour:Object { public GameObject gameObject; public T GetComponent<T>() where T:new()=>throw new NotSupportedException(); }
 public class GameObject { public T AddComponent<T>() where T:new()=>throw new NotSupportedException(); }
 public class AudioClip:Object {}
 public struct Color32 { public byte r,g,b,a; public Color32(byte r,byte g,byte b,byte a){this.r=r;this.g=g;this.b=b;this.a=a;} }
 public class TextAsset:Object { public string text; }
 public static class Application { public static string streamingAssetsPath=>throw new NotSupportedException(); }
 public static class JsonUtility {
  public static T FromJson<T>(string text)=>System.Text.Json.JsonSerializer.Deserialize<T>(text,new System.Text.Json.JsonSerializerOptions {IncludeFields=true});
  public static string ToJson(object value)=>System.Text.Json.JsonSerializer.Serialize(value,value.GetType(),new System.Text.Json.JsonSerializerOptions {IncludeFields=true});
 }
 public class CreateAssetMenuAttribute:Attribute { public string menuName; }
 public class TextAreaAttribute:Attribute { public TextAreaAttribute(int a,int b){} }
 public class RangeAttribute:Attribute { public RangeAttribute(float a,float b){} }
}
public class GatekeeperController:UnityEngine.MonoBehaviour {public GatekeeperScenario scenario;}
public static class GatekeeperDecisionService {public static IGatekeeperDecisionSource Prepare()=>throw new NotSupportedException();}
public static class XybridAvatarPlayer {public static bool ValidateCues(UnityEngine.TextAsset t,UnityEngine.AudioClip a,out string error){throw new NotSupportedException();}}
