using System;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;
using GrasshopperMCP.Properties;

namespace GrasshopperMCP;

/// <summary>
/// Provides plugin metadata for the GrasshopperMCP assembly, including name, description,
/// author information, icon, and versioning details used by Grasshopper.
/// </summary>
public class GrasshopperMCPPluginInfo : GH_AssemblyInfo
{
    public override string Name => "Cassis";

    public override Bitmap Icon => Resources.cassis_icon;

    //Return a short string describing the purpose of this GHA library.
    public override string Description => "Model Context Protocol (MCP) tools and prompts for automating and assisting Grasshopper workflows.";

    public override Guid Id => new("dfbcda10-6fa8-4839-a512-8ee41d0bdf5f");

    //Return a string identifying you or your company.
    public override string AuthorName => "Cassis Contributors";

    //Return a string representing your preferred contact details.
    public override string AuthorContact => "contact: Cassis";

    //Return a string representing the version.  This returns the same version as the assembly.
    public override string AssemblyVersion => GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
}