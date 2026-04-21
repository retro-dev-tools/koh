using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using KohUI;

namespace KohUI.Backends.Dom;

/// <summary>
/// Hand-rolled JSON serialiser for the <see cref="RenderNode"/> /
/// <see cref="Patch"/> wire protocol. Hand-rolled rather than
/// <c>JsonSerializer.Serialize</c> because:
///
/// <list type="bullet">
///   <item>We only emit four patch shapes and a recursive node — the
///         surface is small enough that a direct writer is shorter
///         than the <c>JsonDerivedType</c> + source-gen ceremony.</item>
///   <item>Props are a <c>string → object?</c> map; <see cref="Utf8JsonWriter"/>
///         with explicit type-dispatch is AOT-friendly in a way the
///         reflection-based object writer is not.</item>
/// </list>
///
/// <para>
/// Non-primitive prop values (like <c>Func&lt;TMsg&gt;</c> for event
/// handlers) are surfaced as <c>true</c> booleans — the client only
/// needs to know <em>that</em> a handler exists so it can attach a
/// listener; the handler itself stays on the server.
/// </para>
/// </summary>
internal static class JsonPatchSerializer
{
    public static byte[] SerializeInitial(RenderNode root)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using var w = new Utf8JsonWriter(buffer);
        w.WriteStartObject();
        w.WriteString("op", "replace");
        w.WriteString("path", "");
        w.WritePropertyName("node");
        WriteNode(w, root);
        w.WriteEndObject();
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] SerializePatches(IReadOnlyList<Patch> patches)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using var w = new Utf8JsonWriter(buffer);
        w.WriteStartObject();
        w.WriteString("op", "batch");
        w.WriteStartArray("patches");
        foreach (var p in patches) WritePatch(w, p);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WritePatch(Utf8JsonWriter w, Patch p)
    {
        w.WriteStartObject();
        switch (p)
        {
            case ReplaceNode r:
                w.WriteString("op", "replace");
                w.WriteString("path", r.Path);
                w.WritePropertyName("node");
                WriteNode(w, r.Node);
                break;

            case UpdateProps u:
                w.WriteString("op", "props");
                w.WriteString("path", u.Path);
                w.WritePropertyName("set");
                WriteProps(w, u.Changed);
                w.WriteStartArray("remove");
                foreach (var k in u.Removed) w.WriteStringValue(k);
                w.WriteEndArray();
                break;

            case InsertChild i:
                w.WriteString("op", "insert");
                w.WriteString("path", i.Path);
                w.WriteNumber("index", i.Index);
                w.WritePropertyName("node");
                WriteNode(w, i.Node);
                break;

            case RemoveChild rm:
                w.WriteString("op", "remove");
                w.WriteString("path", rm.Path);
                w.WriteNumber("index", rm.Index);
                break;

            default:
                throw new NotSupportedException($"Unknown patch type: {p.GetType().Name}");
        }
        w.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter w, RenderNode node)
    {
        w.WriteStartObject();
        w.WriteString("type", node.Type);
        if (node.Key is not null) w.WriteString("key", node.Key);
        w.WritePropertyName("props");
        WriteProps(w, node.Props);
        if (node.Children.Length > 0)
        {
            w.WriteStartArray("children");
            foreach (var child in node.Children) WriteNode(w, child);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteProps(Utf8JsonWriter w, ImmutableDictionary<string, object?> props)
    {
        w.WriteStartObject();
        foreach (var kv in props)
        {
            w.WritePropertyName(kv.Key);
            WritePropValue(w, kv.Value);
        }
        w.WriteEndObject();
    }

    private static void WritePropValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case string s: w.WriteStringValue(s); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            case Delegate:
                // Event handlers aren't serialised — the client only needs
                // to know the node has a handler attached for this slot.
                w.WriteBooleanValue(true);
                break;
            default:
                // Fall back to ToString() for unknown types to keep the
                // protocol loose rather than throwing at wire time.
                w.WriteStringValue(v.ToString() ?? "");
                break;
        }
    }
}
