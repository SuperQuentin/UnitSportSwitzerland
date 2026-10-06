using System;

namespace UnitSport.Core;

/// <summary>
/// Puts a procedural model in the model viewer (<c>--models</c>, <c>src/Avatar/ModelViewer.cs</c>),
/// which finds these by reflection: tag the builder, never edit a list.
///
/// <para>
/// On a static method with no parameters returning an <c>ArrayMesh</c> / <c>Mesh</c> (shown with the
/// shared vertex-colour material), a <c>Node3D</c> (shown as is: its own materials, parts, script),
/// or an <c>IEnumerable&lt;(string Name, Func&lt;Node3D&gt; Make)&gt;</c> for a set of variants
/// (each tuple named on its own, <see cref="Name"/> is then a prefix or null).
/// A builder that needs arguments gets a small parameterless factory next to it.
/// </para>
/// <para>
/// Untagged parameterless static mesh builders still show, under "Unlisted";
/// <c>--models,&lt;dir&gt;</c> also lists the builder classes with nothing in the viewer.
/// See <c>docs/notes/avatar/model-viewer.md</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ShowcaseAttribute : Attribute
{
    public ShowcaseAttribute(string category, string? name = null)
    {
        Category = category;
        Name = name;
    }

    /// <summary>The viewer's category: Up/Down move between them. Reuse an existing one when it fits.</summary>
    public string Category { get; }

    /// <summary>The model's label; null takes the method's name.</summary>
    public string? Name { get; }

    /// <summary>
    /// A mesh with a figure in it: shown with <c>HumanMeshBuilder.FigureMaterial</c>, which draws
    /// faces and clothes' finishes, instead of the plain vertex-colour material.
    /// </summary>
    public bool Figure { get; set; }
}
