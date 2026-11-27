using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }
    public enum FindObjectsSortMode { None, InstanceID }
    public enum LODFadeMode { None = 0, CrossFade = 1, SpeedTree = 2 }

    public struct EntityId
    {
        private readonly int _id;
        internal EntityId(int id) { _id = id; }
        public override string ToString() => _id.ToString();
    }

    public class Object
    {
        private static int _nextId = 1000;
        private readonly int _id = ++_nextId;
        public string name { get; set; } = string.Empty;
        public int GetInstanceID() => _id;
        public EntityId GetEntityId() => new EntityId(_id);
        public static void DestroyImmediate(Object obj) { }
        public static T[] FindObjectsByType<T>() where T : Object => Array.Empty<T>();
        public static T[] FindObjectsByType<T>(FindObjectsSortMode sortMode) where T : Object => Array.Empty<T>();
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; }
        public Transform transform => gameObject?.transform;
        public T GetComponent<T>() where T : Component => gameObject != null ? gameObject.GetComponent<T>() : null;
        public T GetComponentInParent<T>() where T : Component => gameObject != null ? gameObject.GetComponentInParent<T>() : null;
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component =>
            gameObject != null ? gameObject.GetComponentsInChildren<T>(includeInactive) : Array.Empty<T>();
    }

    public class MonoBehaviour : Component { }

    public sealed class Transform : Component
    {
        public Transform parent { get; set; }
        public Vector3 localScale { get; set; } = new Vector3(1f, 1f, 1f);
        public Vector3 lossyScale => parent != null
            ? new Vector3(localScale.x * parent.lossyScale.x, localScale.y * parent.lossyScale.y, localScale.z * parent.lossyScale.z)
            : localScale;
    }

    public sealed class GameObject : Object
    {
        private readonly List<Component> _components = new List<Component>();
        public Transform transform { get; }

        public GameObject() : this("GameObject") { }
        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform { gameObject = this };
            _components.Add(transform);
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var c = new T { gameObject = this };
            _components.Add(c);
            return c;
        }

        public T GetComponent<T>() where T : Component
        {
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T match)
                    return match;
            }
            return null;
        }

        public T GetComponentInParent<T>() where T : Component
        {
            GameObject cur = this;
            while (cur != null)
            {
                T found = cur.GetComponent<T>();
                if (found != null) return found;
                cur = cur.transform.parent?.gameObject;
            }
            return null;
        }

        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component
        {
            var list = new List<T>();
            T self = GetComponent<T>();
            if (self != null) list.Add(self);
            return list.ToArray();
        }

        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            var go = new GameObject(type.ToString());
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = type + "Mesh" };
            var mr = go.AddComponent<MeshRenderer>();
            var defaultShader = new Shader("Universal Render Pipeline/Lit", true);
            defaultShader._declaredKeywords.Add("DOTS_INSTANCING_ON");
            mr.sharedMaterial = new Material(defaultShader)
            {
                name = "Default-Material",
                enableInstancing = true,
                renderQueue = 2000
            };
            return go;
        }
    }

    public sealed class Mesh : Object
    {
        private readonly Dictionary<int, uint> _subMeshIndices = new Dictionary<int, uint> { { 0, 36u } };
        public int subMeshCount { get; set; } = 1;
        public uint GetIndexCount(int submesh) => _subMeshIndices.TryGetValue(submesh, out uint c) ? c : 36u;
        public void SetSubMeshIndexCountForTest(int submesh, uint indexCount) => _subMeshIndices[submesh] = indexCount;
    }

    public sealed class MeshFilter : Component
    {
        public Mesh sharedMesh { get; set; }
    }

    public sealed class LODGroup : Component
    {
        public LODFadeMode fadeMode { get; set; } = LODFadeMode.None;
        public bool animateCrossFading { get; set; }
    }

    public sealed class Shader : Object
    {
        internal readonly HashSet<string> _declaredKeywords = new HashSet<string>(StringComparer.Ordinal);
        public bool isSupported { get; }

        internal Shader(string name, bool supported = true)
        {
            this.name = name ?? "Hidden/InternalErrorShader";
            isSupported = supported;
        }

        public LocalKeywordSpace keywordSpace => new LocalKeywordSpace(_declaredKeywords);

        public static Shader Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var s = new Shader(name, true);
            if (name.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) >= 0)
                s._declaredKeywords.Add("DOTS_INSTANCING_ON");
            return s;
        }
    }

    public sealed class Material : Object
    {
        public Shader shader { get; set; }
        public bool enableInstancing { get; set; }
        public int renderQueue { get; set; } = 2000;

        public Material(Shader shader)
        {
            this.shader = shader;
            name = shader != null ? shader.name : "Material";
        }
    }

    public sealed class MaterialPropertyBlock
    {
        private bool _hasValues;
        public bool isEmpty => !_hasValues;
        public void SetColor(string name, Color value) => _hasValues = true;
        public void Clear() => _hasValues = false;
    }

    public sealed class MeshRenderer : Component
    {
        private MaterialPropertyBlock _mpb;
        private Material[] _sharedMaterials = Array.Empty<Material>();

        public Material sharedMaterial
        {
            get => (_sharedMaterials != null && _sharedMaterials.Length > 0) ? _sharedMaterials[0] : null;
            set => _sharedMaterials = value != null ? new[] { value } : Array.Empty<Material>();
        }

        public Material[] sharedMaterials
        {
            get => _sharedMaterials;
            set => _sharedMaterials = value ?? Array.Empty<Material>();
        }

        public LightProbeUsage lightProbeUsage { get; set; } = LightProbeUsage.BlendProbes;
        public Transform probeAnchor { get; set; }
        public Mesh additionalVertexStreams { get; set; }
        public Mesh enlightenVertexStream { get; set; }

        public bool HasPropertyBlock() => _mpb != null && !_mpb.isEmpty;
        public void SetPropertyBlock(MaterialPropertyBlock properties) => _mpb = properties;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
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
    }

    public static class Mathf
    {
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
    }

    public static class Application
    {
        public static bool isBatchMode => false;
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public sealed class GUIStyle { }

    public sealed class GUISkin
    {
        public GUIStyle box { get; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
        public static Color backgroundColor { get; set; }
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

    public struct LocalKeyword
    {
        public string name { get; }
        public bool isValid { get; }
        internal LocalKeyword(string name, bool valid)
        {
            this.name = name ?? string.Empty;
            isValid = valid;
        }
    }

    public struct LocalKeywordSpace
    {
        private readonly HashSet<string> _keywords;
        internal LocalKeywordSpace(HashSet<string> keywords) { _keywords = keywords; }
        public LocalKeyword FindKeyword(string name)
        {
            bool valid = _keywords != null && !string.IsNullOrEmpty(name) && _keywords.Contains(name);
            return new LocalKeyword(name, valid);
        }
    }

    public abstract class RenderPipeline
    {
        protected virtual void Render(object context, UnityEngine.Object[] cameras) { }
    }

    public abstract class RenderPipelineAsset : UnityEngine.ScriptableObject
    {
        protected abstract RenderPipeline CreatePipeline();
    }

    public static class GraphicsSettings
    {
        public static RenderPipelineAsset currentRenderPipeline { get; set; }
        public static bool useScriptableRenderPipelineBatching { get; set; } = true;
        public static UnityEngine.Object GetGraphicsSettings() => null;
    }
}

namespace UnityEditor.Rendering
{
    public enum BatchRendererGroupStrippingMode
    {
        KeepIfReferenced = 0,
        StripAll = 1,
        KeepAll = 2
    }

    public static class EditorGraphicsSettings
    {
        public static BatchRendererGroupStrippingMode batchRendererGroupShaderStrippingMode =>
            BatchRendererGroupStrippingMode.KeepAll;
    }
}

namespace Unity.Pipeline.Commands
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CliCommandAttribute : Attribute
    {
        public bool MainThreadRequired { get; set; } = true;
        public string[] Tags { get; set; }
        public CliCommandAttribute(string name, string description = "") { }
        public CliCommandAttribute(string group, string name, string description = "") { }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class CliArgAttribute : Attribute
    {
        public bool Required { get; set; }
        public CliArgAttribute(string name, string description = "") { }
    }
}

namespace UnityEditor
{
    public enum MessageType { None, Info, Warning, Error }

    [Flags]
    public enum StaticEditorFlags
    {
        Nothing = 0,
        BatchingStatic = 4
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    public static class EditorApplication
    {
        public static Action update;
        public static void Exit(int returnValue) { }
    }

    public static class AssemblyReloadEvents
    {
        public static Action beforeAssemblyReload;
    }

    public static class EditorGUIUtility
    {
        public static bool isProSkin => true;
    }

    public static class ShaderUtil
    {
        public static UnityEngine.Shader CreateShaderAsset(string source, bool performUpkeep = false)
        {
            string shaderName = "Custom/GeneratedBrgTestShader";
            Match nm = Regex.Match(source ?? string.Empty, @"Shader\s+""([^""]+)""");
            if (nm.Success)
                shaderName = nm.Groups[1].Value;

            var shader = new UnityEngine.Shader(shaderName, true);
            MatchCollection pragmas = Regex.Matches(source ?? string.Empty, @"#pragma\s+(?:multi_compile|shader_feature)\w*\s+([^\r\n]+)");
            for (int p = 0; p < pragmas.Count; p++)
            {
                string[] toks = pragmas[p].Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < toks.Length; k++)
                {
                    if (toks[k] != "_")
                        shader._declaredKeywords.Add(toks[k].Trim());
                }
            }
            return shader;
        }
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

    public static class GameObjectUtility
    {
        public static bool AreStaticEditorFlagsSet(UnityEngine.GameObject go, StaticEditorFlags flags) => false;
        public static StaticEditorFlags GetStaticEditorFlags(UnityEngine.GameObject go) => StaticEditorFlags.Nothing;
        public static void SetStaticEditorFlags(UnityEngine.GameObject go, StaticEditorFlags flags) { }
    }

    public static class EditorUtility
    {
        public static UnityEngine.Object InstanceIDToObject(int instanceID) => null;
        public static UnityEngine.Object EntityIdToObject(UnityEngine.EntityId entityId) => null;
        public static bool IsPersistent(UnityEngine.Object target) => false;
        public static void SetDirty(UnityEngine.Object target) { }
        public static string SaveFilePanel(string title, string directory, string defaultName, string extension) => string.Empty;
        public static void RevealInFinder(string path) { }
    }

    public static class AssetDatabase
    {
        public static string[] FindAssets(string filter) => Array.Empty<string>();
        public static string[] FindAssets(string filter, string[] searchInFolders) => Array.Empty<string>();
        public static string GUIDToAssetPath(string guid) => string.Empty;
        public static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object => null;
        public static string GetAssetPath(UnityEngine.Object assetObject) => string.Empty;
    }

    public static class Selection
    {
        public static UnityEngine.Object activeObject { get; set; }
    }

    public sealed class SerializedProperty
    {
        public int intValue { get; set; }
    }

    public sealed class SerializedObject : IDisposable
    {
        public SerializedObject(UnityEngine.Object obj) { }
        public SerializedProperty FindProperty(string propertyPath) => new SerializedProperty();
        public bool ApplyModifiedProperties() => true;
        public void Dispose() { }
    }

    public static class Undo
    {
        public static void RecordObject(UnityEngine.Object objectToUndo, string name) { }
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
    public enum CollectionVirtualizationMethod { FixedHeight, DynamicHeight }
    public enum SelectionType { None, Single, Multiple }
    public enum AlternatingRowBackground { None, ContentOnly, All }

    public sealed class StyleSheet : UnityEngine.ScriptableObject { }

    public sealed class VisualElementStyleSheetSet
    {
        public void Add(StyleSheet styleSheet) { }
    }

    public sealed class Painter2D
    {
        public float lineWidth { get; set; }
        public UnityEngine.Color strokeColor { get; set; }
        public UnityEngine.Color fillColor { get; set; }
        public void BeginPath() { }
        public void MoveTo(UnityEngine.Vector2 pos) { }
        public void LineTo(UnityEngine.Vector2 pos) { }
        public void ClosePath() { }
        public void Stroke() { }
        public void Fill() { }
    }

    public sealed class MeshGenerationContext
    {
        public Painter2D painter2D { get; } = new Painter2D();
    }

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
        private readonly HashSet<string> _classes = new HashSet<string>(StringComparer.Ordinal);
        public string name { get; set; } = string.Empty;
        public string tooltip { get; set; } = string.Empty;
        public object userData { get; set; }
        public UnityEngine.Rect contentRect { get; set; } = new UnityEngine.Rect(0, 0, 320, 20);
        public Action<MeshGenerationContext> generateVisualContent { get; set; }
        public VisualElementStyleSheetSet styleSheets { get; set; } = new VisualElementStyleSheetSet();
        public IStyle style { get; } = new IStyle();
        public int childCount => _children.Count;
        public IReadOnlyList<VisualElement> Children => _children;
        public void Add(VisualElement child) { if (child != null) _children.Add(child); }
        public void Clear() => _children.Clear();
        public void AddToClassList(string className) { if (!string.IsNullOrEmpty(className)) _classes.Add(className); }
        public void MarkDirtyRepaint() { }
    }

    public sealed class Column
    {
        public string name { get; set; }
        public string title { get; set; }
        public Length width { get; set; }
        public Length minWidth { get; set; }
        public bool stretchable { get; set; }
        public Func<VisualElement> makeCell { get; set; }
        public Action<VisualElement, int> bindCell { get; set; }
    }

    public sealed class Columns
    {
        private readonly List<Column> _cols = new List<Column>();
        public int Count => _cols.Count;
        public void Add(Column c) { if (c != null) _cols.Add(c); }
    }

    public class MultiColumnListView : VisualElement
    {
        public float fixedItemHeight { get; set; } = 24f;
        public CollectionVirtualizationMethod virtualizationMethod { get; set; }
        public SelectionType selectionType { get; set; }
        public AlternatingRowBackground showAlternatingRowBackgrounds { get; set; }
        public Columns columns { get; } = new Columns();
        public IList itemsSource { get; set; }
        public void Rebuild() { }
    }

    public class Label : VisualElement
    {
        public string text { get; set; }
        public Label() : this(string.Empty) { }
        public Label(string text) { this.text = text ?? string.Empty; }
    }

    public class Button : VisualElement
    {
        public event Action clicked;
        public string text { get; set; }
        public Button() : this(null) { }
        public Button(Action clickEvent) { if (clickEvent != null) clicked += clickEvent; }
        public void InvokeClick() => clicked?.Invoke();
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
