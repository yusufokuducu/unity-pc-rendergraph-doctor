using System;
using System.Collections.Generic;
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
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
        public static Color red => new Color(1f, 0f, 0f, 1f);
        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
    }

    public static class Debug
    {
        public static void Log(object message) { }
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
        public UnityEngine.UIElements.VisualElement rootVisualElement { get; } = new UnityEngine.UIElements.VisualElement();
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

namespace UnityEngine.UIElements
{
    public enum FlexDirection { Column, ColumnReverse, Row, RowReverse }
    public enum Align { Auto, FlexStart, Center, FlexEnd, Stretch }
    public enum Justify { FlexStart, Center, FlexEnd, SpaceBetween, SpaceAround }
    public enum Wrap { NoWrap, Wrap, WrapReverse }
    public enum WhiteSpace { Normal, NoWrap }
    public enum HelpBoxMessageType { None, Info, Warning, Error }

    public struct Length
    {
        public float value { get; set; }
        public Length(float value) { this.value = value; }
        public static Length Percent(float value) => new Length(value);
        public static implicit operator Length(float v) => new Length(v);
    }

    public struct StyleLength
    {
        public Length value { get; set; }
        public StyleLength(Length v) { value = v; }
        public StyleLength(float v) { value = new Length(v); }
        public static implicit operator StyleLength(float v) => new StyleLength(v);
        public static implicit operator StyleLength(Length v) => new StyleLength(v);
    }

    public struct StyleFloat
    {
        public float value { get; set; }
        public StyleFloat(float v) { value = v; }
        public static implicit operator StyleFloat(float v) => new StyleFloat(v);
    }

    public struct StyleColor
    {
        public UnityEngine.Color value { get; set; }
        public StyleColor(UnityEngine.Color c) { value = c; }
        public static implicit operator StyleColor(UnityEngine.Color c) => new StyleColor(c);
    }

    public struct StyleEnum<T> where T : struct, Enum
    {
        public T value { get; set; }
        public StyleEnum(T v) { value = v; }
        public static implicit operator StyleEnum<T>(T v) => new StyleEnum<T>(v);
    }

    public sealed class IStyle
    {
        public StyleEnum<FlexDirection> flexDirection { get; set; }
        public StyleEnum<Align> alignItems { get; set; }
        public StyleEnum<Justify> justifyContent { get; set; }
        public StyleEnum<Wrap> flexWrap { get; set; }
        public StyleFloat flexGrow { get; set; }
        public StyleFloat flexShrink { get; set; }
        public StyleLength width { get; set; }
        public StyleLength height { get; set; }
        public StyleLength minWidth { get; set; }
        public StyleLength minHeight { get; set; }
        public StyleLength maxHeight { get; set; }
        public StyleLength marginTop { get; set; }
        public StyleLength marginBottom { get; set; }
        public StyleLength marginLeft { get; set; }
        public StyleLength marginRight { get; set; }
        public StyleLength paddingTop { get; set; }
        public StyleLength paddingBottom { get; set; }
        public StyleLength paddingLeft { get; set; }
        public StyleLength paddingRight { get; set; }
        public StyleLength borderTopWidth { get; set; }
        public StyleLength borderBottomWidth { get; set; }
        public StyleLength borderLeftWidth { get; set; }
        public StyleLength borderRightWidth { get; set; }
        public StyleColor backgroundColor { get; set; }
        public StyleColor borderTopColor { get; set; }
        public StyleColor borderBottomColor { get; set; }
        public StyleColor borderLeftColor { get; set; }
        public StyleColor borderRightColor { get; set; }
        public StyleColor color { get; set; }
        public StyleLength fontSize { get; set; }
        public StyleEnum<WhiteSpace> whiteSpace { get; set; }
    }

    public class VisualElement
    {
        private readonly List<VisualElement> _children = new List<VisualElement>();
        public string name { get; set; } = string.Empty;
        public string tooltip { get; set; } = string.Empty;
        public IStyle style { get; } = new IStyle();
        public int childCount => _children.Count;
        public IReadOnlyList<VisualElement> Children => _children;
        public void Add(VisualElement child) { if (child != null) _children.Add(child); }
        public void Clear() => _children.Clear();
    }

    public class Label : VisualElement
    {
        public string text { get; set; }
        public Label() : this(string.Empty) { }
        public Label(string text) { this.text = text ?? string.Empty; }
    }

    public class Button : VisualElement
    {
        private readonly Action _clickEvent;
        public string text { get; set; }
        public Button() : this(null) { }
        public Button(Action clickEvent) { _clickEvent = clickEvent; }
        public void InvokeClick() => _clickEvent?.Invoke();
    }

    public sealed class ChangeEvent<T>
    {
        public T previousValue { get; set; }
        public T newValue { get; set; }
    }

    public delegate void EventCallback<in TEventType>(TEventType evt);

    public class TextField : VisualElement
    {
        private string _value = string.Empty;
        private EventCallback<ChangeEvent<string>> _callback;
        public string label { get; set; }
        public string value
        {
            get => _value;
            set
            {
                string prev = _value;
                _value = value ?? string.Empty;
                _callback?.Invoke(new ChangeEvent<string> { previousValue = prev, newValue = _value });
            }
        }
        public TextField() : this(string.Empty) { }
        public TextField(string label) { this.label = label; }
        public void SetValueWithoutNotify(string newValue) { _value = newValue ?? string.Empty; }
        public void RegisterValueChangedCallback(EventCallback<ChangeEvent<string>> callback) { _callback += callback; }
    }

    public class ScrollView : VisualElement { }

    public class ProgressBar : VisualElement
    {
        public string title { get; set; }
        public float lowValue { get; set; } = 0f;
        public float highValue { get; set; } = 100f;
        public float value { get; set; }
    }

    public class HelpBox : VisualElement
    {
        public string text { get; set; }
        public HelpBoxMessageType messageType { get; set; }
        public HelpBox() { }
        public HelpBox(string text, HelpBoxMessageType messageType)
        {
            this.text = text;
            this.messageType = messageType;
        }
    }
}
