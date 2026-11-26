using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Defines an optional, procedurally drawn top-center expandable Boss Health Bar style.
    /// Attached to a giant via GiantRigDef.healthBarDef; unfolds on spawn and persists until killed or despawned.
    /// </summary>
    public class GiantHealthBarDef : Def
    {
        public float topScreenMargin = 16f;
        public float barWidth = 540f;
        public float mainBarHeight = 26f;
        public float subRowHeight = 24f;
        public float subMeterWidth = 156f;
        public float stackSpacing = 10f;
        public int maxVisibleSubRows = 12;

        public float enterExpandSeconds = 0.48f;
        public float exitCollapseSeconds = 0.40f;
        public float drawerToggleSpeed = 12f;
        public float damageLagDecayPerSecond = 0.55f;

        public bool defaultExpanded = false;
        public bool showExpandButton = true;
        public bool clickBarToSelectAndJump = true;
        public bool hideFullHealthMinorLimbs = false;
        public bool drawHierarchyLines = true;

        public Color backgroundColor = new Color(0.086f, 0.098f, 0.118f, 0.92f);
        public Color drawerBackgroundColor = new Color(0.078f, 0.090f, 0.110f, 0.90f);
        public Color borderColor = new Color(0.32f, 0.35f, 0.40f, 1.00f);
        public Color lagBarColor = new Color(0.85f, 0.76f, 0.52f, 0.95f);
        public Color healthyFillColor = new Color(0.33f, 0.61f, 0.35f, 1.00f);
        public Color woundedFillColor = new Color(0.78f, 0.57f, 0.23f, 1.00f);
        public Color criticalFillColor = new Color(0.74f, 0.27f, 0.25f, 1.00f);
        public Color textColor = new Color(0.96f, 0.97f, 0.98f, 1.00f);

        // Optional custom skinning, ornaments, crest & secondary power/phase sub-bar
        public string subtitleKey;
        public string subtitleText;

        public string barFillTexPath;
        public string barFillActiveTexPath;
        public string barFrameTexPath;
        public Color activeBorderColor = Color.clear;
        public Color activeTextColor = Color.clear;

        public string leftOrnamentTexPath;
        public string rightOrnamentTexPath;
        public string leftOrnamentActiveTexPath;
        public string rightOrnamentActiveTexPath;
        public string leftOrnamentGlowTexPath;
        public string rightOrnamentGlowTexPath;
        public Vector2 ornamentSize = Vector2.zero;
        public float ornamentOverlapX = 12f;
        public float ornamentOffsetY = 0f;

        public string centerCrestTexPath;
        public string centerCrestActiveTexPath;
        public string centerCrestGlowTexPath;
        public Vector2 centerCrestSize = Vector2.zero;
        public float centerCrestOffsetY = 0f;

        public bool showSecondaryStateBar = false;
        public float secondaryBarHeight = 6f;
        public Color secondaryBarBgColor = new Color(0.050f, 0.055f, 0.072f, 0.94f);
        public Color secondaryBarFillColor = new Color(0.92f, 0.18f, 0.26f, 1.00f);

        [Unsaved(false)] private bool texturesResolved;
        [Unsaved(false)] private Texture2D cachedBarFillTex;
        [Unsaved(false)] private Texture2D cachedBarFillActiveTex;
        [Unsaved(false)] private Texture2D cachedBarFrameTex;
        [Unsaved(false)] private Texture2D cachedLeftOrnamentTex;
        [Unsaved(false)] private Texture2D cachedRightOrnamentTex;
        [Unsaved(false)] private Texture2D cachedLeftOrnamentActiveTex;
        [Unsaved(false)] private Texture2D cachedRightOrnamentActiveTex;
        [Unsaved(false)] private Texture2D cachedLeftOrnamentGlowTex;
        [Unsaved(false)] private Texture2D cachedRightOrnamentGlowTex;
        [Unsaved(false)] private Texture2D cachedCenterCrestTex;
        [Unsaved(false)] private Texture2D cachedCenterCrestActiveTex;
        [Unsaved(false)] private Texture2D cachedCenterCrestGlowTex;

        public bool HasCustomSkinning =>
            !string.IsNullOrEmpty(barFillTexPath)
            || !string.IsNullOrEmpty(barFillActiveTexPath)
            || !string.IsNullOrEmpty(leftOrnamentTexPath)
            || !string.IsNullOrEmpty(rightOrnamentTexPath)
            || !string.IsNullOrEmpty(centerCrestTexPath)
            || showSecondaryStateBar;

        public bool HasSideOrnaments =>
            ornamentSize.x > 4f && ornamentSize.y > 4f &&
            (!string.IsNullOrEmpty(leftOrnamentTexPath) || !string.IsNullOrEmpty(rightOrnamentTexPath)
             || !string.IsNullOrEmpty(leftOrnamentActiveTexPath) || !string.IsNullOrEmpty(rightOrnamentActiveTexPath));

        public bool HasCenterCrest =>
            centerCrestSize.x > 4f && centerCrestSize.y > 4f &&
            (!string.IsNullOrEmpty(centerCrestTexPath) || !string.IsNullOrEmpty(centerCrestActiveTexPath));

        private static Texture2D LoadTexOptional(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !UnityData.IsInMainThread)
            {
                return null;
            }
            return ContentFinder<Texture2D>.Get(path, reportFailure: false);
        }

        public void EnsureSkinTexturesLoaded()
        {
            if (texturesResolved || !UnityData.IsInMainThread)
            {
                return;
            }
            texturesResolved = true;
            cachedBarFillTex = LoadTexOptional(barFillTexPath);
            cachedBarFillActiveTex = LoadTexOptional(barFillActiveTexPath);
            cachedBarFrameTex = LoadTexOptional(barFrameTexPath);
            cachedLeftOrnamentTex = LoadTexOptional(leftOrnamentTexPath);
            cachedRightOrnamentTex = LoadTexOptional(rightOrnamentTexPath);
            cachedLeftOrnamentActiveTex = LoadTexOptional(leftOrnamentActiveTexPath);
            cachedRightOrnamentActiveTex = LoadTexOptional(rightOrnamentActiveTexPath);
            cachedLeftOrnamentGlowTex = LoadTexOptional(leftOrnamentGlowTexPath);
            cachedRightOrnamentGlowTex = LoadTexOptional(rightOrnamentGlowTexPath);
            cachedCenterCrestTex = LoadTexOptional(centerCrestTexPath);
            cachedCenterCrestActiveTex = LoadTexOptional(centerCrestActiveTexPath);
            cachedCenterCrestGlowTex = LoadTexOptional(centerCrestGlowTexPath);
        }

        public Texture2D BarFillTex { get { EnsureSkinTexturesLoaded(); return cachedBarFillTex; } }
        public Texture2D BarFillActiveTex { get { EnsureSkinTexturesLoaded(); return cachedBarFillActiveTex; } }
        public Texture2D BarFrameTex { get { EnsureSkinTexturesLoaded(); return cachedBarFrameTex; } }
        public Texture2D LeftOrnamentTex { get { EnsureSkinTexturesLoaded(); return cachedLeftOrnamentTex; } }
        public Texture2D RightOrnamentTex { get { EnsureSkinTexturesLoaded(); return cachedRightOrnamentTex; } }
        public Texture2D LeftOrnamentActiveTex { get { EnsureSkinTexturesLoaded(); return cachedLeftOrnamentActiveTex; } }
        public Texture2D RightOrnamentActiveTex { get { EnsureSkinTexturesLoaded(); return cachedRightOrnamentActiveTex; } }
        public Texture2D LeftOrnamentGlowTex { get { EnsureSkinTexturesLoaded(); return cachedLeftOrnamentGlowTex; } }
        public Texture2D RightOrnamentGlowTex { get { EnsureSkinTexturesLoaded(); return cachedRightOrnamentGlowTex; } }
        public Texture2D CenterCrestTex { get { EnsureSkinTexturesLoaded(); return cachedCenterCrestTex; } }
        public Texture2D CenterCrestActiveTex { get { EnsureSkinTexturesLoaded(); return cachedCenterCrestActiveTex; } }
        public Texture2D CenterCrestGlowTex { get { EnsureSkinTexturesLoaded(); return cachedCenterCrestGlowTex; } }

        public Color EvaluateFillColor(float pct, float alphaMultiplier = 1f)
        {
            Color baseCol;
            if (pct <= 0.001f)
            {
                baseCol = new Color(0.36f, 0.16f, 0.16f, 1f);
            }
            else if (pct < 0.35f)
            {
                baseCol = criticalFillColor;
            }
            else if (pct < 0.72f)
            {
                baseCol = woundedFillColor;
            }
            else
            {
                baseCol = healthyFillColor;
            }
            return new Color(baseCol.r, baseCol.g, baseCol.b, baseCol.a * alphaMultiplier);
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string err in base.ConfigErrors())
            {
                yield return err;
            }
            if (barWidth < 80f)
            {
                yield return $"GiantHealthBarDef '{defName}' barWidth ({barWidth}) must be >= 80.";
            }
            if (mainBarHeight < 10f)
            {
                yield return $"GiantHealthBarDef '{defName}' mainBarHeight ({mainBarHeight}) must be >= 10.";
            }
            if (subRowHeight < 12f)
            {
                yield return $"GiantHealthBarDef '{defName}' subRowHeight ({subRowHeight}) must be >= 12.";
            }
            if (enterExpandSeconds < 0f || exitCollapseSeconds < 0f)
            {
                yield return $"GiantHealthBarDef '{defName}' animation durations must be non-negative.";
            }
            if (secondaryBarHeight < 0f)
            {
                yield return $"GiantHealthBarDef '{defName}' secondaryBarHeight ({secondaryBarHeight}) must be >= 0.";
            }
            if (ornamentSize.x < 0f || ornamentSize.y < 0f)
            {
                yield return $"GiantHealthBarDef '{defName}' ornamentSize must be non-negative.";
            }
            if (centerCrestSize.x < 0f || centerCrestSize.y < 0f)
            {
                yield return $"GiantHealthBarDef '{defName}' centerCrestSize must be non-negative.";
            }
        }
    }
}
