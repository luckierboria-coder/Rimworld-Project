param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Path,[string]$Old,[string]$New,[string]$Label)
    $text = (Get-Content $Path -Raw).Replace("`r`n", "`n")
    $oldNorm = $Old.Replace("`r`n", "`n")
    $newNorm = $New.Replace("`r`n", "`n")
    if (-not $text.Contains($oldNorm)) { throw "SMF15 shader patch anchor not found: $Label ($Path)" }
    $text = $text.Replace($oldNorm,$newNorm)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$p = Join-Path $Root 'src/Smf.Mod/Rendering/Shaders/GuiShaderBundleAssembler.cs'

# RimWorld 1.5.4063 runs Unity 2019.4.30f1. Its SerializedFile format is v21,
# while upstream SMF 0.3.12 is intentionally locked to Unity 2022.3.35f1 / v22.
Replace-OrThrow $p @'
    public const string UnityVersion = "2022.3.35f1";
    public const string ShaderTypeHash = "EA424A5FC1E48DB62144DF956F9E1005";
    public const string IndexTypeHash = "97DA5F4688E45A57C8B42D4F42497297";
    public const string ShaderSchemaSha256 = "86751B5763AD8221AEEADBA3EF1703B789CD7A9631EC6CDBA34C07F97E33FFDF";
    public const string IndexSchemaSha256 = "2DFFB8F817A04511817851D1CA8748F90E9EBE0CF0FAB24F004C8CC2D3E9D5A0";
'@ @'
    public const string UnityVersion = "2019.4.30f1";
    public const uint SerializedVersion = 21;
    public const uint BundleVersion = 6;
    public const string ShaderTypeHash = "ED2A06CE7FB8203F6EFB5AB5E0CE20D5";
    public const string IndexTypeHash = "97DA5F4688E45A57C8B42D4F42497297";
    public const string ShaderSchemaSha256 = "6DF5E61E817041DEA67346CC1FDB63DD7E572BBD133DBD979AD201E8146A04CE";
    public const string IndexSchemaSha256 = "49753446D7D95BA7BA86B897A2BB658A911053A01F823F3167D0B2B515FF73B7";
'@ 'Unity 2019 schema/profile constants'

# Never assume Unity 2019's unity_builtin_extra uses the same path IDs as Unity 2022.
Replace-OrThrow $p @'
        var requests = new List<Request>
        {
            new Request(9000, "Hidden/Internal-GUITextureClip", options.GuiNamePrefix + "GUITextureClip", "guitextureclip", false),
            new Request(9002, "Hidden/Internal-GUITexture", options.GuiNamePrefix + "GUITexture", "guitexture", false),
            new Request(9003, "Hidden/Internal-GUITextureBlit", options.GuiNamePrefix + "GUITextureBlit", "guitextureblit", false)
        };
        if (options.IncludePremultipliedCopy)
        {
            requests.Add(new Request(66, "Hidden/BlitCopy", options.CopyShaderName, "blitcopy", true));
        }
'@ @'
        var requests = new List<Request>
        {
            new Request("Hidden/Internal-GUITextureClip", options.GuiNamePrefix + "GUITextureClip", "guitextureclip", false),
            new Request("Hidden/Internal-GUITexture", options.GuiNamePrefix + "GUITexture", "guitexture", false),
            new Request("Hidden/Internal-GUITextureBlit", options.GuiNamePrefix + "GUITextureBlit", "guitextureblit", false)
        };
        if (options.IncludePremultipliedCopy)
        {
            requests.Add(new Request("Hidden/BlitCopy", options.CopyShaderName, "blitcopy", true));
        }
'@ 'resolve built-in shader objects by name'

Replace-OrThrow $p @'
            ValidateSource(source, input.Length);

            var rawObjects = new Dictionary<long, byte[]>();
'@ @'
            ValidateSource(source, input.Length);
            ResolveRequests(source, shaderTemplate, requests);

            var rawObjects = new Dictionary<long, byte[]>();
'@ 'resolve requested shader path IDs after parsing source'

Replace-OrThrow $p @'
                AssetFileInfo info = source.Metadata.GetAssetInfo(request.PathId);
                Require(info != null && info.TypeIdOrIndex == 0, "Expected shader object is absent.");
'@ @'
                AssetFileInfo info = source.Metadata.GetAssetInfo(request.PathId);
                Require(info != null && info.GetTypeId(source) == 48, "Expected shader object is absent.");
'@ 'validate resolved Shader class ID'

# SerializedFile v21 and UnityFS v6 are the correct container generations for Unity 2019.4.
Replace-OrThrow $p 'Header = new AssetsFileHeader { Version = 22, Endianness = false },' 'Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false },' 'output SerializedFile version 21'
Replace-OrThrow $p '                Version = 8,' '                Version = BundleVersion,' 'UnityFS bundle version 6'
Replace-OrThrow $p '                bundle.Header.Version == 8 &&' '                bundle.Header.Version == BundleVersion &&' 'validate UnityFS bundle version 6'
Replace-OrThrow $p '            type.Write(writer, 22, false);' '            type.Write(writer, SerializedVersion, false);' 'write v21 type descriptor'
Replace-OrThrow $p '            source.Header.Version == 22 &&' '            source.Header.Version == SerializedVersion &&' 'validate source SerializedFile v21'
Replace-OrThrow $p '                header.Version == 22 &&' '                header.Version == SerializedVersion &&' 'preflight SerializedFile v21'
Replace-OrThrow $p '                header.DataOffset >= 48 &&' '                header.DataOffset >= 20 &&' 'v21 legacy-width header lower bound'
Replace-OrThrow $p @'
            // Object table entries are 24 bytes each in format 22.
            int count = reader.ReadInt32();
            Require(count > 0 && count < 4096 && reader.Position + 24L * count + 12 <= header.DataOffset,
'@ @'
            // Format 21 uses 8-byte path ID + 4-byte data offset + 4-byte size + 4-byte type index.
            int count = reader.ReadInt32();
            Require(count > 0 && count < 4096 && reader.Position + 20L * count + 12 <= header.DataOffset,
'@ 'v21 object table width'
Replace-OrThrow $p '            reader.Position += 24L * count;' '            reader.Position += 20L * count;' 'skip v21 object table'
Replace-OrThrow $p '            schema.SerializedVersion == 22,' '            schema.SerializedVersion == SerializedVersion,' 'validate generated v21 schemas'

# Locate the four source shaders by their serialized m_Name, rather than 2022 path IDs.
Replace-OrThrow $p @'
    // Unity BlendMode values: 0 Zero, 1 One, 5 SrcAlpha, 10 OneMinusSrcAlpha.
    private static void ValidateBlend(AssetTypeValueField value, Request request)
'@ @'
    private static void ResolveRequests(AssetsFile source, AssetTypeTemplateField template, List<Request> requests)
    {
        var wanted = requests.ToDictionary(r => r.OldName, StringComparer.Ordinal);
        foreach (AssetFileInfo info in source.Metadata.AssetInfos)
        {
            if (info == null) continue;
            int typeId;
            try { typeId = info.GetTypeId(source); }
            catch { continue; }
            if (typeId != 48) continue;

            try
            {
                byte[] raw = ReadObject(source, info);
                AssetTypeValueField value = ParseObject(template, raw);
                string name = Field(value, "m_Name").AsString;
                if (!wanted.TryGetValue(name, out Request request)) continue;
                Require(!request.Resolved, "Duplicate built-in shader name: " + name);
                request.PathId = info.PathId;
                request.Resolved = true;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                // Non-target Shader objects are irrelevant. Target presence is checked below.
            }
        }

        foreach (Request request in requests)
        {
            Require(request.Resolved, "Required Unity 2019 built-in shader is absent: " + request.OldName);
        }
    }

    // Unity BlendMode values: 0 Zero, 1 One, 5 SrcAlpha, 10 OneMinusSrcAlpha.
    private static void ValidateBlend(AssetTypeValueField value, Request request)
'@ 'insert name-based source shader resolver'

Replace-OrThrow $p @'
        public readonly long PathId;
        public readonly string OldName;
'@ @'
        public long PathId;
        public bool Resolved;
        public readonly string OldName;
'@ 'make source path ID runtime-resolved'
Replace-OrThrow $p @'
        public Request(long pathId, string oldName, string newName, string key, bool isCopy)
        {
            PathId = pathId;
            OldName = oldName;
'@ @'
        public Request(string oldName, string newName, string key, bool isCopy)
        {
            PathId = 0;
            Resolved = false;
            OldName = oldName;
'@ 'runtime-resolved Request constructor'

Write-Host 'Applied SimplyMoreFPS Unity 2019.4.30f1 shader/AssetBundle backport: SerializedFile v21, UnityFS v6, 2019 type trees, name-based shader discovery.'
