using System.Collections.Generic;
using System.Threading;

namespace Basis.ModelPickup.Validation
{
    /// <summary>
    /// JSON → <see cref="BasisGltfDocument"/>. Unknown keys are skipped; a known key with the wrong JSON type (or null)
    /// is rejected with its path; arrays are capped before each add; number arrays have exact lengths. Content the
    /// canonical output never carries (animations, cameras, lights, extras, unknown extensions…) is skipped and
    /// recorded in <see cref="BasisGltfDocument.Stripped"/>. Value-range checks wait for the semantic stage, which
    /// only looks at retained objects.
    /// </summary>
    public sealed class BasisGltfJsonBinder
    {
        private const int MaxExtensionNames = 64;
        private const int MaxExtensionNameBytes = 64;
        private const int MaxShortStringBytes = 64;
        public const byte AlphaModeInvalid = 255;

        private readonly BasisJsonReader _r;
        private readonly BasisModelLimits _limits;
        private readonly BasisGltfDocument _doc;
        private readonly CancellationToken _cancellation;
        private readonly List<int> _ints = new List<int>();
        private int _cancelCounter;

        public string Error;
        public BasisGlbErrorKind ErrorKind;

        private BasisGltfJsonBinder(BasisJsonReader reader, in BasisModelLimits limits, byte[] json, CancellationToken cancellation)
        {
            _r = reader;
            _limits = limits;
            _cancellation = cancellation;
            _doc = new BasisGltfDocument { Json = json };
        }

        public static bool TryBind(byte[] json, int start, int length, bool allowTrailingNul, in BasisModelLimits limits,
            CancellationToken cancellation, out BasisGltfDocument document, out BasisGlbErrorKind errorKind, out string error)
        {
            document = null;
            var reader = new BasisJsonReader(json, start, length, BasisJsonReaderLimits.From(limits), allowTrailingNul);
            var binder = new BasisGltfJsonBinder(reader, limits, json, cancellation);
            if (!binder.BindRoot())
            {
                errorKind = binder.ErrorKind;
                error = binder.Error;
                return false;
            }
            errorKind = BasisGlbErrorKind.None;
            error = null;
            document = binder._doc;
            return true;
        }

        // ---- plumbing -------------------------------------------------------------------------------------------

        private bool Next()
        {
            if (_r.Read()) return true;
            return FromReader();
        }

        private bool FromReader()
        {
            if (Error == null)
            {
                Error = _r.Error ?? "JSON could not be read.";
                ErrorKind = _r.ErrorIsLimit ? BasisGlbErrorKind.OverLimit : BasisGlbErrorKind.Malformed;
            }
            return false;
        }

        private bool Fail(string message)
        {
            if (Error == null)
            {
                Error = message;
                ErrorKind = BasisGlbErrorKind.Malformed;
            }
            return false;
        }

        private bool FailLimit(string message)
        {
            if (Error == null)
            {
                Error = message;
                ErrorKind = BasisGlbErrorKind.OverLimit;
            }
            return false;
        }

        private bool Cancelled()
        {
            if (Error == null)
            {
                Error = "Validation was cancelled.";
                ErrorKind = BasisGlbErrorKind.Cancelled;
            }
            return false;
        }

        private bool TickCancellation()
        {
            if (++_cancelCounter < 1024) return true;
            _cancelCounter = 0;
            return !_cancellation.IsCancellationRequested || Cancelled();
        }

        private static string Path(string collection, int index, string member)
        {
            if (collection == null) return member;
            if (index < 0) return collection + "." + member;
            return BasisGlbErrors.Index(collection, index, member);
        }

        private bool SkipValue()
        {
            return Next() && (_r.TrySkipValue() || FromReader());
        }

        private void Flag(BasisGlbStripped flag)
        {
            _doc.Stripped |= flag;
        }

        private bool SkipFlagged(BasisGlbStripped flag)
        {
            _doc.Stripped |= flag;
            return SkipValue();
        }

        private bool ReadInt(string collection, int index, string member, out int value)
        {
            value = 0;
            if (!Next()) return false;
            if (_r.TryGetNonNegativeInt32(out value)) return true;
            return Fail(Path(collection, index, member) + " must be a non-negative integer.");
        }

        private bool ReadUInt(string collection, int index, string member, out long value)
        {
            value = 0;
            if (!Next()) return false;
            if (_r.TryGetUInt32(out uint parsed))
            {
                value = parsed;
                return true;
            }
            return Fail(Path(collection, index, member) + " must be a non-negative integer.");
        }

        private bool ReadFloat(string collection, int index, string member, out float value)
        {
            value = 0f;
            if (!Next()) return false;
            if (_r.TryGetSingle(out value)) return true;
            return Fail(Path(collection, index, member) + " must be a finite number.");
        }

        private bool ReadBool(string collection, int index, string member, out bool value)
        {
            value = false;
            if (!Next()) return false;
            if (_r.TryGetBoolean(out value)) return true;
            return Fail(Path(collection, index, member) + " must be true or false.");
        }

        private bool ReadString(string collection, int index, string member, int maxBytes, out string value)
        {
            value = null;
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.String) return Fail(Path(collection, index, member) + " must be a string.");
            if (_r.TryGetString(maxBytes, out value)) return true;
            return FailLimit(Path(collection, index, member) + " is longer than " + BasisGlbErrors.N(maxBytes) + " bytes.");
        }

        private bool ReadUriRange(string collection, int index, out int start, out int length, out bool hasEscapes)
        {
            start = 0;
            length = 0;
            hasEscapes = false;
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.String) return Fail(Path(collection, index, "uri") + " must be a string.");
            start = _r.ValueStart;
            length = _r.ValueLength;
            hasEscapes = _r.ValueHasEscapes;
            return true;
        }

        private bool BeginObject(string collection, int index, string member)
        {
            if (!Next()) return false;
            if (_r.Kind == BasisJsonTokenKind.BeginObject) return true;
            return Fail(Path(collection, index, member) + " must be an object.");
        }

        /// <summary>Reads the next member name; false with <paramref name="done"/> at the object's end.</summary>
        private bool NextMember(out bool done)
        {
            done = false;
            if (!Next()) return false;
            if (_r.Kind == BasisJsonTokenKind.EndObject)
            {
                done = true;
                return true;
            }
            return true;
        }

        private bool ReadFloatArray(string collection, int index, string member, float[] destination)
        {
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginArray) return Fail(Path(collection, index, member) + " must be an array of numbers.");
            int count = 0;
            while (true)
            {
                if (!Next()) return false;
                if (_r.Kind == BasisJsonTokenKind.EndArray) break;
                if (count >= destination.Length || !_r.TryGetSingle(out float value))
                {
                    return Fail(Path(collection, index, member) + " must be exactly " + destination.Length + " finite numbers.");
                }
                destination[count++] = value;
            }
            if (count != destination.Length)
            {
                return Fail(Path(collection, index, member) + " must be exactly " + destination.Length + " finite numbers.");
            }
            return true;
        }

        /// <summary>Accessor min/max: 1–16 JSON numbers. Only the type is checked; the scanner recomputes POSITION bounds.</summary>
        private bool CheckNumberArray(string collection, int index, string member)
        {
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginArray) return Fail(Path(collection, index, member) + " must be an array of numbers.");
            int count = 0;
            while (true)
            {
                if (!Next()) return false;
                if (_r.Kind == BasisJsonTokenKind.EndArray) break;
                if (_r.Kind != BasisJsonTokenKind.Number || ++count > 16)
                {
                    return Fail(Path(collection, index, member) + " must be 1 to 16 numbers.");
                }
            }
            return count >= 1 || Fail(Path(collection, index, member) + " must be 1 to 16 numbers.");
        }

        private bool ReadIntArray(string collection, int index, string member, out int[] values)
        {
            values = null;
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginArray) return Fail(Path(collection, index, member) + " must be an array of indices.");
            _ints.Clear();
            while (true)
            {
                if (!Next()) return false;
                if (_r.Kind == BasisJsonTokenKind.EndArray) break;
                if (_ints.Count >= _limits.MaxRawArrayLength)
                {
                    return FailLimit(Path(collection, index, member) + " has more than " + BasisGlbErrors.N(_limits.MaxRawArrayLength) + " entries.");
                }
                if (!_r.TryGetNonNegativeInt32(out int value))
                {
                    return Fail(Path(collection, index, member) + " must contain only non-negative integers.");
                }
                _ints.Add(value);
            }
            values = _ints.ToArray();
            return true;
        }

        /// <summary>Iterates an array of objects; <paramref name="bindElement"/> is called at each BeginObject.</summary>
        private bool BindObjectArray(string collection, System.Func<int, bool> bindElement)
        {
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginArray) return Fail(collection + " must be an array.");
            int index = 0;
            while (true)
            {
                if (!Next()) return false;
                if (_r.Kind == BasisJsonTokenKind.EndArray) return true;
                if (index >= _limits.MaxRawArrayLength)
                {
                    return FailLimit(collection + " has more than " + BasisGlbErrors.N(_limits.MaxRawArrayLength) + " entries.");
                }
                if (_r.Kind != BasisJsonTokenKind.BeginObject) return Fail(BasisGlbErrors.Index(collection, index) + " must be an object.");
                if (!bindElement(index)) return false;
                if (!TickCancellation()) return false;
                index++;
            }
        }

        /// <summary>Skips an extensions object, flagging each member through <paramref name="classify"/>.</summary>
        private bool SkipExtensions(string collection, int index, System.Func<BasisGlbStripped> classify)
        {
            if (!BeginObject(collection, index, "extensions")) return false;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                Flag(classify());
                if (!SkipValue()) return false;
            }
        }

        // ---- root -------------------------------------------------------------------------------------------------

        private bool BindRoot()
        {
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginObject) return Fail("The glTF JSON root must be an object.");
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) break;
                bool ok;
                if (_r.PropertyNameIs("asset")) ok = BindAsset();
                else if (_r.PropertyNameIs("extensionsUsed")) ok = BindExtensionNames("extensionsUsed", _doc.ExtensionsUsed);
                else if (_r.PropertyNameIs("extensionsRequired")) ok = BindExtensionNames("extensionsRequired", _doc.ExtensionsRequired);
                else if (_r.PropertyNameIs("scene"))
                {
                    ok = ReadInt(null, -1, "scene", out _doc.Scene);
                    _doc.HasScene = true;
                }
                else if (_r.PropertyNameIs("scenes")) ok = BindObjectArray("scenes", BindScene);
                else if (_r.PropertyNameIs("nodes")) ok = BindObjectArray("nodes", BindNode);
                else if (_r.PropertyNameIs("meshes")) ok = BindObjectArray("meshes", BindMesh);
                else if (_r.PropertyNameIs("accessors")) ok = BindObjectArray("accessors", BindAccessor);
                else if (_r.PropertyNameIs("bufferViews")) ok = BindObjectArray("bufferViews", BindBufferView);
                else if (_r.PropertyNameIs("buffers")) ok = BindObjectArray("buffers", BindBuffer);
                else if (_r.PropertyNameIs("materials")) ok = BindObjectArray("materials", BindMaterial);
                else if (_r.PropertyNameIs("textures")) ok = BindObjectArray("textures", BindTexture);
                else if (_r.PropertyNameIs("images")) ok = BindObjectArray("images", BindImage);
                else if (_r.PropertyNameIs("samplers")) ok = BindObjectArray("samplers", BindSampler);
                else if (_r.PropertyNameIs("skins")) ok = BindObjectArray("skins", BindSkin);
                else if (_r.PropertyNameIs("animations")) ok = SkipFlagged(BasisGlbStripped.Animations);
                else if (_r.PropertyNameIs("cameras")) ok = SkipFlagged(BasisGlbStripped.Cameras);
                else if (_r.PropertyNameIs("extensions")) ok = SkipExtensions(null, -1, ClassifyRootExtension);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.End) return Fail("JSON has data after the root object.");
            return true;
        }

        private BasisGlbStripped ClassifyRootExtension()
        {
            if (_r.PropertyNameIs("KHR_lights_punctual")) return BasisGlbStripped.Lights;
            if (_r.PropertyNameIs("KHR_materials_variants")) return BasisGlbStripped.MaterialVariants;
            return BasisGlbStripped.OtherExtensions;
        }

        private bool BindAsset()
        {
            if (!BeginObject(null, -1, "asset")) return false;
            _doc.HasAsset = true;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("version")) ok = ReadString(null, -1, "asset.version", MaxShortStringBytes, out _doc.AssetVersion);
                else if (_r.PropertyNameIs("minVersion")) ok = ReadString(null, -1, "asset.minVersion", MaxShortStringBytes, out _doc.AssetMinVersion);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue(); // generator, copyright: never emitted
                if (!ok) return false;
            }
        }

        private bool BindExtensionNames(string member, List<string> names)
        {
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginArray) return Fail(member + " must be an array of strings.");
            names.Clear();
            while (true)
            {
                if (!Next()) return false;
                if (_r.Kind == BasisJsonTokenKind.EndArray) return true;
                int index = names.Count;
                if (index >= MaxExtensionNames) return FailLimit(member + " has more than " + MaxExtensionNames + " entries.");
                if (_r.Kind != BasisJsonTokenKind.String) return Fail(BasisGlbErrors.Index(member, index) + " must be a string.");
                if (!_r.TryGetString(MaxExtensionNameBytes, out string name))
                {
                    return FailLimit(BasisGlbErrors.Index(member, index) + " is longer than " + MaxExtensionNameBytes + " bytes.");
                }
                for (int i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], name, System.StringComparison.Ordinal))
                    {
                        return Fail(BasisGlbErrors.Index(member, index) + " repeats an earlier entry.");
                    }
                }
                names.Add(name);
            }
        }

        // ---- scenes and nodes -------------------------------------------------------------------------------------

        private bool BindScene(int index)
        {
            var scene = new BasisGltfScene();
            _doc.Scenes.Add(scene);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("nodes")) ok = ReadIntArray("scenes", index, "nodes", out scene.Nodes);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindNode(int index)
        {
            var node = new BasisGltfNode();
            _doc.Nodes.Add(node);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("children")) ok = ReadIntArray("nodes", index, "children", out node.Children);
                else if (_r.PropertyNameIs("mesh")) ok = ReadInt("nodes", index, "mesh", out node.Mesh);
                else if (_r.PropertyNameIs("skin")) ok = ReadInt("nodes", index, "skin", out node.Skin);
                else if (_r.PropertyNameIs("matrix"))
                {
                    node.Matrix = new float[16];
                    node.HasMatrix = true;
                    ok = ReadFloatArray("nodes", index, "matrix", node.Matrix);
                }
                else if (_r.PropertyNameIs("translation"))
                {
                    var t = new float[3];
                    ok = ReadFloatArray("nodes", index, "translation", t);
                    node.HasT = true;
                    node.Tx = t[0];
                    node.Ty = t[1];
                    node.Tz = t[2];
                }
                else if (_r.PropertyNameIs("rotation"))
                {
                    var q = new float[4];
                    ok = ReadFloatArray("nodes", index, "rotation", q);
                    node.HasR = true;
                    node.Rx = q[0];
                    node.Ry = q[1];
                    node.Rz = q[2];
                    node.Rw = q[3];
                }
                else if (_r.PropertyNameIs("scale"))
                {
                    var s = new float[3];
                    ok = ReadFloatArray("nodes", index, "scale", s);
                    node.HasS = true;
                    node.Sx = s[0];
                    node.Sy = s[1];
                    node.Sz = s[2];
                }
                else if (_r.PropertyNameIs("camera")) ok = SkipFlagged(BasisGlbStripped.Cameras);
                else if (_r.PropertyNameIs("weights")) ok = SkipFlagged(BasisGlbStripped.MorphTargets);
                else if (_r.PropertyNameIs("extensions")) ok = SkipExtensions("nodes", index, ClassifyNodeExtension);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private BasisGlbStripped ClassifyNodeExtension()
        {
            if (_r.PropertyNameIs("KHR_lights_punctual")) return BasisGlbStripped.Lights;
            if (_r.PropertyNameIs("EXT_mesh_gpu_instancing")) return BasisGlbStripped.Instancing;
            return BasisGlbStripped.OtherExtensions;
        }

        // ---- meshes -----------------------------------------------------------------------------------------------

        private bool BindMesh(int index)
        {
            var mesh = new BasisGltfMesh();
            _doc.Meshes.Add(mesh);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("primitives")) ok = BindPrimitives(index, mesh);
                else if (_r.PropertyNameIs("weights")) ok = SkipFlagged(BasisGlbStripped.MorphTargets);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindPrimitives(int meshIndex, BasisGltfMesh mesh)
        {
            if (!Next()) return false;
            if (_r.Kind != BasisJsonTokenKind.BeginArray) return Fail(BasisGlbErrors.Index("meshes", meshIndex, "primitives") + " must be an array.");
            while (true)
            {
                if (!Next()) return false;
                if (_r.Kind == BasisJsonTokenKind.EndArray) return true;
                int index = mesh.Primitives.Count;
                if (index >= _limits.MaxRawArrayLength)
                {
                    return FailLimit(BasisGlbErrors.Index("meshes", meshIndex, "primitives") + " has more than "
                        + BasisGlbErrors.N(_limits.MaxRawArrayLength) + " entries.");
                }
                if (_r.Kind != BasisJsonTokenKind.BeginObject) return Fail(BasisGlbErrors.Primitive(meshIndex, index) + " must be an object.");
                var primitive = new BasisGltfPrimitive();
                mesh.Primitives.Add(primitive);
                if (!BindPrimitive(meshIndex, index, primitive)) return false;
            }
        }

        private bool BindPrimitive(int meshIndex, int index, BasisGltfPrimitive primitive)
        {
            string path = BasisGlbErrors.Primitive(meshIndex, index);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("attributes")) ok = BindAttributes(path, primitive);
                else if (_r.PropertyNameIs("indices")) ok = ReadInt(path, -1, "indices", out primitive.Indices);
                else if (_r.PropertyNameIs("material")) ok = ReadInt(path, -1, "material", out primitive.Material);
                else if (_r.PropertyNameIs("mode"))
                {
                    ok = ReadInt(path, -1, "mode", out primitive.Mode);
                    if (ok && primitive.Mode > 6) ok = Fail(path + ".mode must be 0 to 6.");
                }
                else if (_r.PropertyNameIs("targets"))
                {
                    primitive.HasTargets = true;
                    ok = SkipFlagged(BasisGlbStripped.MorphTargets);
                }
                else if (_r.PropertyNameIs("extensions")) ok = SkipExtensions(path, -1, ClassifyPrimitiveExtension);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private BasisGlbStripped ClassifyPrimitiveExtension()
        {
            if (_r.PropertyNameIs("KHR_draco_mesh_compression")) return BasisGlbStripped.CompressionFallbackUsed;
            if (_r.PropertyNameIs("KHR_materials_variants")) return BasisGlbStripped.MaterialVariants;
            return BasisGlbStripped.OtherExtensions;
        }

        private bool BindAttributes(string path, BasisGltfPrimitive primitive)
        {
            if (!BeginObject(path, -1, "attributes")) return false;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                int slot = ClassifyAttribute(out BasisGlbStripped stripped);
                if (!ReadInt(path, -1, "attributes value", out int accessor)) return false;
                switch (slot)
                {
                    case 0: primitive.Position = accessor; break;
                    case 1: primitive.Normal = accessor; break;
                    case 2: primitive.Tangent = accessor; break;
                    case 3: primitive.TexCoord0 = accessor; break;
                    case 4: primitive.TexCoord1 = accessor; break;
                    case 5: primitive.Color0 = accessor; break;
                    case 6: primitive.Joints0 = accessor; break;
                    case 7: primitive.Weights0 = accessor; break;
                    default:
                        if (stripped == BasisGlbStripped.ExtraUvSets) primitive.HasExtraUv = true;
                        else primitive.HasExtraStreams = true;
                        Flag(stripped);
                        break;
                }
            }
        }

        private int ClassifyAttribute(out BasisGlbStripped stripped)
        {
            stripped = BasisGlbStripped.ExtraVertexStreams;
            if (_r.PropertyNameIs("POSITION")) return 0;
            if (_r.PropertyNameIs("NORMAL")) return 1;
            if (_r.PropertyNameIs("TANGENT")) return 2;
            if (_r.PropertyNameIs("TEXCOORD_0")) return 3;
            if (_r.PropertyNameIs("TEXCOORD_1")) return 4;
            if (_r.PropertyNameIs("COLOR_0")) return 5;
            if (_r.PropertyNameIs("JOINTS_0")) return 6;
            if (_r.PropertyNameIs("WEIGHTS_0")) return 7;
            System.ReadOnlySpan<byte> name = _r.PropertyName;
            const string texcoord = "TEXCOORD_";
            if (name.Length > texcoord.Length)
            {
                bool match = true;
                for (int i = 0; i < texcoord.Length; i++)
                {
                    if (name[i] != texcoord[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) stripped = BasisGlbStripped.ExtraUvSets;
            }
            return -1;
        }

        // ---- accessors, views, buffers ------------------------------------------------------------------------------

        private bool BindAccessor(int index)
        {
            var accessor = new BasisGltfAccessor();
            _doc.Accessors.Add(accessor);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("bufferView")) ok = ReadInt("accessors", index, "bufferView", out accessor.BufferView);
                else if (_r.PropertyNameIs("byteOffset")) ok = ReadUInt("accessors", index, "byteOffset", out accessor.ByteOffset);
                else if (_r.PropertyNameIs("componentType")) ok = ReadInt("accessors", index, "componentType", out accessor.ComponentType);
                else if (_r.PropertyNameIs("normalized")) ok = ReadBool("accessors", index, "normalized", out accessor.Normalized);
                else if (_r.PropertyNameIs("count")) ok = ReadInt("accessors", index, "count", out accessor.Count);
                else if (_r.PropertyNameIs("type"))
                {
                    ok = ReadString("accessors", index, "type", MaxShortStringBytes, out string type);
                    if (ok) accessor.Type = ParseType(type);
                }
                else if (_r.PropertyNameIs("max")) ok = CheckNumberArray("accessors", index, "max");
                else if (_r.PropertyNameIs("min")) ok = CheckNumberArray("accessors", index, "min");
                else if (_r.PropertyNameIs("sparse"))
                {
                    accessor.HasSparse = true;
                    ok = SkipValue();
                }
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        /// <summary>Exact uppercase only. glTFast parses case-insensitively, which does not matter: it only reads our output.</summary>
        private static byte ParseType(string type)
        {
            switch (type)
            {
                case "SCALAR": return BasisGltfType.Scalar;
                case "VEC2": return BasisGltfType.Vec2;
                case "VEC3": return BasisGltfType.Vec3;
                case "VEC4": return BasisGltfType.Vec4;
                case "MAT2": return BasisGltfType.Mat2;
                case "MAT3": return BasisGltfType.Mat3;
                case "MAT4": return BasisGltfType.Mat4;
                default: return BasisGltfType.None;
            }
        }

        private bool BindBufferView(int index)
        {
            var view = new BasisGltfBufferView();
            _doc.BufferViews.Add(view);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("buffer")) ok = ReadInt("bufferViews", index, "buffer", out view.Buffer);
                else if (_r.PropertyNameIs("byteOffset")) ok = ReadUInt("bufferViews", index, "byteOffset", out view.ByteOffset);
                else if (_r.PropertyNameIs("byteLength")) ok = ReadUInt("bufferViews", index, "byteLength", out view.ByteLength);
                else if (_r.PropertyNameIs("byteStride")) ok = ReadInt("bufferViews", index, "byteStride", out view.ByteStride);
                else if (_r.PropertyNameIs("target")) ok = ReadInt("bufferViews", index, "target", out _);
                else if (_r.PropertyNameIs("extensions")) ok = SkipExtensions("bufferViews", index, ClassifyViewExtension);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private BasisGlbStripped ClassifyViewExtension()
        {
            if (_r.PropertyNameIs("EXT_meshopt_compression") || _r.PropertyNameIs("KHR_meshopt_compression"))
            {
                return BasisGlbStripped.CompressionFallbackUsed;
            }
            return BasisGlbStripped.OtherExtensions;
        }

        private bool BindBuffer(int index)
        {
            var buffer = new BasisGltfBuffer();
            _doc.Buffers.Add(buffer);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("byteLength")) ok = ReadUInt("buffers", index, "byteLength", out buffer.ByteLength);
                else if (_r.PropertyNameIs("uri"))
                {
                    ok = ReadUriRange("buffers", index, out buffer.UriStart, out buffer.UriLength, out buffer.UriHasEscapes);
                    buffer.HasUri = true;
                }
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        // ---- materials, textures, images, samplers, skins --------------------------------------------------------------

        private bool BindMaterial(int index)
        {
            var material = new BasisGltfMaterial();
            _doc.Materials.Add(material);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("pbrMetallicRoughness")) ok = BindPbr(index, material);
                else if (_r.PropertyNameIs("normalTexture")) ok = BindTextureRef(index, "normalTexture", 1, ref material.NormalTex);
                else if (_r.PropertyNameIs("occlusionTexture")) ok = BindTextureRef(index, "occlusionTexture", 2, ref material.OcclusionTex);
                else if (_r.PropertyNameIs("emissiveTexture")) ok = BindTextureRef(index, "emissiveTexture", 0, ref material.EmissiveTex);
                else if (_r.PropertyNameIs("emissiveFactor")) ok = ReadFloatArray("materials", index, "emissiveFactor", material.Emissive);
                else if (_r.PropertyNameIs("alphaMode"))
                {
                    ok = ReadString("materials", index, "alphaMode", MaxShortStringBytes, out string mode);
                    if (ok)
                    {
                        material.AlphaMode = mode == "OPAQUE" ? (byte)0 : mode == "MASK" ? (byte)1 : mode == "BLEND" ? (byte)2 : AlphaModeInvalid;
                    }
                }
                else if (_r.PropertyNameIs("alphaCutoff")) ok = ReadFloat("materials", index, "alphaCutoff", out material.AlphaCutoff);
                else if (_r.PropertyNameIs("doubleSided")) ok = ReadBool("materials", index, "doubleSided", out material.DoubleSided);
                else if (_r.PropertyNameIs("extensions")) ok = BindMaterialExtensions(index, material);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindPbr(int index, BasisGltfMaterial material)
        {
            if (!BeginObject("materials", index, "pbrMetallicRoughness")) return false;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("baseColorFactor")) ok = ReadFloatArray("materials", index, "pbrMetallicRoughness.baseColorFactor", material.BaseColor);
                else if (_r.PropertyNameIs("baseColorTexture")) ok = BindTextureRef(index, "pbrMetallicRoughness.baseColorTexture", 0, ref material.BaseColorTex);
                else if (_r.PropertyNameIs("metallicFactor")) ok = ReadFloat("materials", index, "pbrMetallicRoughness.metallicFactor", out material.Metallic);
                else if (_r.PropertyNameIs("roughnessFactor")) ok = ReadFloat("materials", index, "pbrMetallicRoughness.roughnessFactor", out material.Roughness);
                else if (_r.PropertyNameIs("metallicRoughnessTexture")) ok = BindTextureRef(index, "pbrMetallicRoughness.metallicRoughnessTexture", 0, ref material.MetallicRoughnessTex);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.MaterialExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindMaterialExtensions(int index, BasisGltfMaterial material)
        {
            if (!BeginObject("materials", index, "extensions")) return false;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("KHR_materials_unlit"))
                {
                    ok = BeginObject("materials", index, "extensions.KHR_materials_unlit") && (_r.TrySkipValue() || FromReader());
                    material.Unlit = true;
                }
                else if (_r.PropertyNameIs("KHR_materials_emissive_strength"))
                {
                    // glTFast 6.14.1 has no reader for it, so stripping keeps the render identical; it is still validated.
                    Flag(BasisGlbStripped.MaterialExtensions);
                    ok = BindEmissiveStrength(index, material);
                }
                else ok = SkipFlagged(BasisGlbStripped.MaterialExtensions);
                if (!ok) return false;
            }
        }

        private bool BindEmissiveStrength(int index, BasisGltfMaterial material)
        {
            if (!BeginObject("materials", index, "extensions.KHR_materials_emissive_strength")) return false;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("emissiveStrength"))
                {
                    ok = ReadFloat("materials", index, "extensions.KHR_materials_emissive_strength.emissiveStrength", out material.EmissiveStrength);
                    material.HasEmissiveStrength = true;
                }
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        /// <param name="factorKind">0 none, 1 "scale" (normalTexture), 2 "strength" (occlusionTexture).</param>
        private bool BindTextureRef(int materialIndex, string member, int factorKind, ref BasisGltfTextureRef reference)
        {
            if (!BeginObject("materials", materialIndex, member)) return false;
            reference = BasisGltfTextureRef.Absent();
            reference.Present = true;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("index")) ok = ReadInt("materials", materialIndex, member + ".index", out reference.Index);
                else if (_r.PropertyNameIs("texCoord")) ok = ReadInt("materials", materialIndex, member + ".texCoord", out reference.TexCoord);
                else if (factorKind == 1 && _r.PropertyNameIs("scale")) ok = ReadFloat("materials", materialIndex, member + ".scale", out reference.ScaleOrStrength);
                else if (factorKind == 2 && _r.PropertyNameIs("strength")) ok = ReadFloat("materials", materialIndex, member + ".strength", out reference.ScaleOrStrength);
                else if (_r.PropertyNameIs("extensions")) ok = BindTextureRefExtensions(materialIndex, member, ref reference);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindTextureRefExtensions(int materialIndex, string member, ref BasisGltfTextureRef reference)
        {
            if (!BeginObject("materials", materialIndex, member + ".extensions")) return false;
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("KHR_texture_transform")) ok = BindTextureTransform(materialIndex, member, ref reference);
                else ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                if (!ok) return false;
            }
        }

        private bool BindTextureTransform(int materialIndex, string member, ref BasisGltfTextureRef reference)
        {
            string path = member + ".extensions.KHR_texture_transform";
            if (!BeginObject("materials", materialIndex, path)) return false;
            reference.HasTransform = true;
            var pair = new float[2];
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("offset"))
                {
                    ok = ReadFloatArray("materials", materialIndex, path + ".offset", pair);
                    reference.OffsetU = pair[0];
                    reference.OffsetV = pair[1];
                }
                else if (_r.PropertyNameIs("scale"))
                {
                    ok = ReadFloatArray("materials", materialIndex, path + ".scale", pair);
                    reference.ScaleU = pair[0];
                    reference.ScaleV = pair[1];
                }
                else if (_r.PropertyNameIs("rotation")) ok = ReadFloat("materials", materialIndex, path + ".rotation", out reference.Rotation);
                else if (_r.PropertyNameIs("texCoord")) ok = ReadInt("materials", materialIndex, path + ".texCoord", out reference.TransformTexCoord);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindTexture(int index)
        {
            var texture = new BasisGltfTexture();
            _doc.Textures.Add(texture);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("sampler")) ok = ReadInt("textures", index, "sampler", out texture.Sampler);
                else if (_r.PropertyNameIs("source")) ok = ReadInt("textures", index, "source", out texture.Source);
                else if (_r.PropertyNameIs("extensions")) ok = SkipExtensions("textures", index, ClassifyTextureExtension);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private BasisGlbStripped ClassifyTextureExtension()
        {
            if (_r.PropertyNameIs("KHR_texture_basisu") || _r.PropertyNameIs("EXT_texture_webp")
                || _r.PropertyNameIs("EXT_texture_avif") || _r.PropertyNameIs("MSFT_texture_dds"))
            {
                return BasisGlbStripped.CompressionFallbackUsed;
            }
            return BasisGlbStripped.OtherExtensions;
        }

        private bool BindImage(int index)
        {
            var image = new BasisGltfImage();
            _doc.Images.Add(image);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("uri"))
                {
                    ok = ReadUriRange("images", index, out image.UriStart, out image.UriLength, out image.UriHasEscapes);
                    image.HasUri = true;
                }
                else if (_r.PropertyNameIs("mimeType")) ok = ReadString("images", index, "mimeType", MaxShortStringBytes, out image.MimeType);
                else if (_r.PropertyNameIs("bufferView")) ok = ReadInt("images", index, "bufferView", out image.BufferView);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindSampler(int index)
        {
            var sampler = new BasisGltfSampler();
            _doc.Samplers.Add(sampler);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("magFilter")) ok = ReadInt("samplers", index, "magFilter", out sampler.MagFilter);
                else if (_r.PropertyNameIs("minFilter")) ok = ReadInt("samplers", index, "minFilter", out sampler.MinFilter);
                else if (_r.PropertyNameIs("wrapS")) ok = ReadInt("samplers", index, "wrapS", out sampler.WrapS);
                else if (_r.PropertyNameIs("wrapT")) ok = ReadInt("samplers", index, "wrapT", out sampler.WrapT);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }

        private bool BindSkin(int index)
        {
            var skin = new BasisGltfSkin();
            _doc.Skins.Add(skin);
            while (true)
            {
                if (!NextMember(out bool done)) return false;
                if (done) return true;
                bool ok;
                if (_r.PropertyNameIs("inverseBindMatrices")) ok = ReadInt("skins", index, "inverseBindMatrices", out skin.InverseBindMatrices);
                else if (_r.PropertyNameIs("skeleton")) ok = ReadInt("skins", index, "skeleton", out skin.Skeleton);
                else if (_r.PropertyNameIs("joints")) ok = ReadIntArray("skins", index, "joints", out skin.Joints);
                else if (_r.PropertyNameIs("extensions")) ok = SkipFlagged(BasisGlbStripped.OtherExtensions);
                else if (_r.PropertyNameIs("extras")) ok = SkipFlagged(BasisGlbStripped.Extras);
                else ok = SkipValue();
                if (!ok) return false;
            }
        }
    }
}
