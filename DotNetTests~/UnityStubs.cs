using System;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public enum PrimitiveType { Cube = 3 }
    public enum FindObjectsSortMode { None = 0 }

    public class Object
    {
        public string name { get; set; } = string.Empty;
        public int GetInstanceID() => GetHashCode();
        public static void DestroyImmediate(Object obj) { }
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode) where T : Object => Array.Empty<T>();
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; }
        public Transform transform => gameObject?.transform;
        public T GetComponent<T>() where T : Component => gameObject?.GetComponent<T>();
        public T GetComponentInParent<T>() where T : Component => null;
    }

    public sealed class Transform : Component
    {
        public Vector3 localScale { get; set; } = new Vector3(1f, 1f, 1f);
        public Vector3 lossyScale => localScale;
    }

    public sealed class Mesh : Object
    {
        public uint GetIndexCount(int submesh) => 36;
    }

    public sealed class MeshFilter : Component
    {
        public Mesh sharedMesh { get; set; } = new Mesh();
    }

    public sealed class Shader : Object
    {
        public Shader() { name = "Universal Render Pipeline/Lit"; }
    }

    public sealed class Material : Object
    {
        public Shader shader { get; set; } = new Shader();
        public bool enableInstancing { get; set; } = true;
        public int renderQueue { get; set; } = 2000;
    }

    public sealed class MaterialPropertyBlock
    {
        public void SetColor(string name, Color value) { }
    }

    public sealed class LODGroup : Component { }

    public sealed class MeshRenderer : Component
    {
        private MaterialPropertyBlock _mpb;
        public Material sharedMaterial { get; set; } = new Material { name = "Default-Lit" };
        public LightProbeUsage lightProbeUsage { get; set; } = LightProbeUsage.BlendProbes;
        public bool HasPropertyBlock() => _mpb != null;
        public void SetPropertyBlock(MaterialPropertyBlock block) => _mpb = block;
    }

    public sealed class GameObject : Object
    {
        public Transform transform { get; } = new Transform();
        private readonly MeshFilter _mf = new MeshFilter();
        private readonly MeshRenderer _mr = new MeshRenderer();

        public GameObject(string name = "GameObject")
        {
            this.name = name;
            transform.gameObject = this;
            _mf.gameObject = this;
            _mr.gameObject = this;
        }

        public T GetComponent<T>() where T : Component
        {
            if (typeof(T) == typeof(MeshFilter)) return _mf as T;
            if (typeof(T) == typeof(MeshRenderer)) return _mr as T;
            if (typeof(T) == typeof(Transform)) return transform as T;
            return null;
        }

        public static GameObject CreatePrimitive(PrimitiveType type) => new GameObject(type.ToString());
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public struct Color
    {
        public static Color red => new Color();
    }

    public sealed class GUIStyle { }

    public sealed class GUISkin
    {
        public GUIStyle box { get; set; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
    }

    public sealed class GUILayoutOption { }

    public static class GUILayout
    {
        public static int Toolbar(int selected, string[] texts, params GUILayoutOption[] options) => selected;
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static GUILayoutOption Height(float h) => new GUILayoutOption();
        public static GUILayoutOption Width(float w) => new GUILayoutOption();
    }
}

namespace UnityEngine.Rendering
{
    public enum LightProbeUsage
    {
        Off = 0,
        BlendProbes = 1,
        UseProxyVolume = 2,
        CustomProvided = 4
    }
}

namespace UnityEditor
{
    [Flags]
    public enum StaticEditorFlags
    {
        BatchingStatic = 4
    }

    public enum MessageType { None, Info, Warning, Error }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    public class EditorWindow : UnityEngine.ScriptableObject
    {
        public UnityEngine.Vector2 minSize { get; set; }
        public static T GetWindow<T>(string title) where T : EditorWindow, new() => new T();
        public void Show() { }
    }

    public static class EditorStyles
    {
        public static UnityEngine.GUIStyle boldLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle miniLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle wordWrappedMiniLabel { get; } = new UnityEngine.GUIStyle();
        public static UnityEngine.GUIStyle helpBox { get; } = new UnityEngine.GUIStyle();
    }

    public static class EditorGUILayout
    {
        public static void Space(float width = 6f) { }
        public static void LabelField(string label, params object[] args) { }
        public static void HelpBox(string message, MessageType type) { }
        public static void BeginHorizontal() { }
        public static void EndHorizontal() { }
        public static void BeginVertical(params object[] args) { }
        public static void EndVertical() { }
        public static UnityEngine.Vector2 BeginScrollView(UnityEngine.Vector2 pos) => pos;
        public static void EndScrollView() { }
    }

    public static class EditorUtility
    {
        public static UnityEngine.Object InstanceIDToObject(int instanceId) => null;
        public static void SetDirty(UnityEngine.Object target) { }
        public static string SaveFilePanel(string title, string directory, string defaultName, string extension) => string.Empty;
        public static void RevealInFinder(string path) { }
    }

    public static class Selection
    {
        public static UnityEngine.Object activeObject { get; set; }
    }

    public static class Undo
    {
        public static void RecordObject(UnityEngine.Object target, string name) { }
    }

    public static class GameObjectUtility
    {
        public static bool AreStaticEditorFlagsSet(UnityEngine.GameObject go, StaticEditorFlags flags) => false;
        public static StaticEditorFlags GetStaticEditorFlags(UnityEngine.GameObject go) => 0;
        public static void SetStaticEditorFlags(UnityEngine.GameObject go, StaticEditorFlags flags) { }
    }
}
