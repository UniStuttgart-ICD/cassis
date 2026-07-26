using System.Collections.Generic;

namespace Cassis;

public static class ToolCategories
{
    public static readonly Dictionary<string, string[]> Categories = new()
    {
        ["Scripts"]     = new[] { "List_CSharp_Scripts", "Get_CSharp_Script", "Get_CSharp_Script_Errors",
                                   "List_Python_Scripts", "Get_Python_Script", "Get_Python_Script_Errors",
                                   "Edit_CSharp_Script", "Edit_Python_Script", "Edit_Script",
                                   "Add_CSharp_Script_Component", "Modify_Script_Component_Parameters",
                                   "Add_Script_Parameter", "Remove_Script_Parameter", "Get_Parameter_TypeHints" },
        ["Components"]  = new[] { "Get_AllComponents", "Get_ComponentCount", "GetDetailedComponentInfo",
                                   "GetDetailedComponentInfoById", "Get_Components_In_Group",
                                   "Get_Component_Output", "Set_Component_Value", "Set_Component_Enabled",
                                   "Set_Component_Preview", "Get_Component_State",
                                   "AddComponent", "Move_Component" },
        ["Connections"] = new[] { "GetAllConnections", "Connect_Components_By_Name",
                                   "ConnectComponentsWithValidation", "ValidateConnection",
                                   "Connect_Components", "ReconnectByName" },
        ["Panels"]      = new[] { "List_Panels", "Get_Panel_Text", "Set_Panel_Text" },
        ["Solver"]      = new[] { "Toggle_BooleanToggle", "ForceDocumentSolution", "GetSolutionState", "ExpireComponent",
                                   "RecomputeScriptComponents", "Toggle_SolverExecute", "Toggle_SolverReset" },
        ["Viewport"]    = new[] { "List_Viewports", "Capture_Viewport", "Orbit_Object", "Capture_Canvas", "Save_HiRes_Canvas", "Manage_NamedViews" },
        ["Document"]    = new[] { "LoadDocument", "SaveDocument", "CloseDocument", "NewDocument",
                                   "GetDocumentInfo", "ListOpenDocuments", "Canvas_Snapshot" },
        ["Diagnostics"] = new[] { "GetSystemHealth", "RunHealthCheck", "ListHealthChecks" },
        ["Prompts"]     = new[] { "CreateGrasshopperDefinition", "OptimizeGrasshopperDefinition",
                                   "TroubleshootGrasshopper", "LearnGrasshopperConcept",
                                   "AnalyzeGrasshopperDefinition" },
    };

    public static readonly HashSet<string> DefaultEnabled = new()
    {
        "List_CSharp_Scripts", "Get_CSharp_Script", "Edit_CSharp_Script", "Get_CSharp_Script_Errors",
        "List_Python_Scripts", "Get_Python_Script", "Edit_Python_Script", "Get_Python_Script_Errors",
        "Edit_Script",
        "Get_AllComponents", "Get_ComponentCount", "Get_Component_Output",
        "GetAllConnections", "List_Panels", "Get_Panel_Text", "Set_Panel_Text",
        "Get_Components_In_Group", "AddComponent", "GetSolutionState",
        "Orbit_Object", "Capture_Viewport", "List_Viewports", "Manage_NamedViews",
        "Get_Parameter_TypeHints",
        "Set_Component_Enabled", "Set_Component_Preview", "Get_Component_State",
        "LoadDocument", "SaveDocument", "CloseDocument", "GetDocumentInfo", "ListOpenDocuments",
        "Canvas_Snapshot",
    };

    /// <summary>All known tool names, flat.</summary>
    public static IEnumerable<string> AllTools()
    {
        foreach (var cat in Categories.Values)
            foreach (var name in cat)
                yield return name;
    }
}
