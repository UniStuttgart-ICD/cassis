using System.ComponentModel;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.AI;

namespace GrasshopperMCP.Tools;

/// <summary>
/// Grasshopper-specific prompts for AI assistance with parametric design and visual programming.
/// </summary>
[McpServerPromptType]
public static class GrasshopperPrompts
{
    /// <summary>
    /// Creates a comprehensive prompt for generating Grasshopper definitions.
    /// </summary>
    [McpServerPrompt]
    [Description("Creates a structured prompt for generating complete Grasshopper visual programming definitions with implementation details")]
    public static ChatMessage CreateGrasshopperDefinition(
        [Description("Description of what to create in Grasshopper")]
        string description,
        [Description("Complexity level: 'beginner', 'intermediate', or 'advanced'")]
        string complexity = "intermediate")
    {
        var complexityGuidance = complexity.ToLower() switch
        {
            "beginner" => "Focus on basic components and clear, simple connections. Provide detailed explanations for each step.",
            "advanced" => "Include advanced techniques like data trees, custom scripting, and optimization strategies.",
            _ => "Balance clarity with functionality, using intermediate-level components and techniques."
        };

        return new ChatMessage(ChatRole.User,
            $"""
             Create a complete Grasshopper definition that {description}.

             COMPLEXITY LEVEL: {complexity} - {complexityGuidance}

             Please provide a comprehensive solution including:

             ## 1. CONCEPTUAL OVERVIEW
             - High-level design strategy and approach
             - Key parametric relationships and dependencies
             - Expected inputs and outputs

             ## 2. COMPONENT BREAKDOWN
             - List all required components with their exact names/types
             - Purpose and role of each component in the definition
             - Suggested canvas layout and grouping strategy

             ## 3. IMPLEMENTATION PLAN
             Provide step-by-step instructions using these MCP tools:
             - `AddComponent(type, x, y)` - Add components (Point, Circle, Line, Move, Rotate, Scale, etc.)
             - `AddPythonScriptComponent(script, x, y)` - Add Python scripts for custom logic
             - `AddCSharpScriptComponent(script, x, y)` - Add C# scripts for performance-critical operations
             - `SetComponentValue(componentId, parameterName, value)` - Set parameter values
             - `ConnectComponents(sourceComponentId, sourceOutputIndex, targetComponentId, targetInputIndex)` - Wire components (indices are 0-based)
             - `GetComponentInfo(componentId)` - Inspect component parameters and metadata
             - `GetComponentRuntimeInfo(componentId)` - Check runtime status and errors
             - `ModifyScriptComponentParameters(componentId, inputs, outputs)` - Configure script parameters
             - `SetComponentScript(componentId, script)` - Update script content

             ## 4. PARAMETER CONFIGURATION
             - Default values and recommended ranges for all parameters
             - Critical parameter relationships and constraints
             - Performance considerations for parameter changes

             ## 5. DATA FLOW ARCHITECTURE
             - Input data requirements and validation
             - Data tree structure and list management
             - Output formatting and downstream compatibility

             ## 6. SCRIPTING COMPONENTS (if needed)
             - Complete, functional Python or C# code
             - Input/output parameter definitions
             - Error handling and edge cases
             
             **C# Script Component Guidelines:**
             - Use default parameter names: 'A', 'B', 'C' for outputs (avoid reserved keywords like 'out')
             - Configure parameters with `ModifyScriptComponentParameters` if custom names needed
             - Always test with `GetComponentRuntimeInfo` to check for compilation errors
             - Common issues: reserved keywords, case sensitivity, parameter configuration timing

             ## 7. TESTING & VALIDATION
             - Recommended test cases and input values
             - Expected geometric or numerical outputs
             - Common failure modes and troubleshooting

             Make the solution production-ready, well-documented, and easily modifiable for future iterations.
             """);
    }

    /// <summary>
    /// Creates a comprehensive prompt for analyzing and optimizing Grasshopper definitions.
    /// </summary>
    [McpServerPrompt]
    [Description("Creates a detailed optimization prompt for improving Grasshopper definition performance, clarity, and maintainability")]
    public static ChatMessage OptimizeGrasshopperDefinition(
        [Description("Current definition description, issues, or performance problems")]
        string currentDefinition,
        [Description("Primary optimization goal: 'performance', 'clarity', 'maintainability', or 'all'")]
        string focus = "all")
    {
        var focusGuidance = focus.ToLower() switch
        {
            "performance" => "Prioritize computational efficiency, memory usage, and execution speed optimizations.",
            "clarity" => "Focus on visual organization, documentation, and making the definition easier to understand.",
            "maintainability" => "Emphasize modularity, reusability, and ease of future modifications.",
            _ => "Balance performance, clarity, and maintainability for overall improvement."
        };

        return new ChatMessage(ChatRole.User,
            $"""
             Analyze and optimize this Grasshopper definition: {currentDefinition}

             OPTIMIZATION FOCUS: {focus} - {focusGuidance}

             ## COMPREHENSIVE ANALYSIS REQUIRED:

             ### 1. PERFORMANCE AUDIT
             - Identify computational bottlenecks and expensive operations
             - Analyze data tree complexity and unnecessary iterations
             - Review component placement in execution order
             - Assess memory usage patterns and potential leaks
             - Check for redundant calculations and duplicate geometry

             ### 2. ARCHITECTURAL REVIEW
             - Evaluate overall data flow efficiency
             - Identify opportunities for component consolidation
             - Review parameter and connection topology
             - Assess potential for parallelization or caching
             - Check for circular dependencies or inefficient loops

             ### 3. CODE QUALITY (for script components)
             - Review Python/C# scripts for optimization opportunities
             - Check for proper error handling and edge cases
             - Evaluate algorithm efficiency and data structures
             - Assess compatibility with Grasshopper's data model

             ### 4. VISUAL ORGANIZATION
             - Suggest canvas layout improvements for clarity
             - Recommend grouping and clustering strategies
             - Evaluate component naming and documentation
             - Assess use of colors, notes, and visual hierarchy

             ### 5. SPECIFIC RECOMMENDATIONS
             Provide actionable optimization steps using MCP tools:
             - Components to remove, replace, or reconfigure
             - New connections or parameter adjustments
             - Script optimizations with complete code
             - Canvas reorganization strategies

             ### 6. ALTERNATIVE APPROACHES
             - Suggest completely different methodologies if beneficial
             - Identify newer or more efficient component alternatives
             - Propose modular approaches for complex definitions
             - Consider hybrid solutions mixing components and scripts

             ### 7. FUTURE-PROOFING
             - Ensure compatibility across Rhino/Grasshopper versions
             - Design for scalability and parameter range expansion
             - Document assumptions and limitations
             - Provide upgrade paths for enhanced functionality

             Include specific metrics where possible (performance gains, complexity reduction, etc.) and prioritize changes by impact vs. effort.
             """);
    }

    /// <summary>
    /// Creates a systematic prompt for diagnosing and resolving Grasshopper issues.
    /// </summary>
    [McpServerPrompt]
    [Description("Creates a comprehensive troubleshooting prompt for systematically diagnosing and fixing Grasshopper problems")]
    public static ChatMessage TroubleshootGrasshopper(
        [Description("Detailed description of the problem, error messages, or unexpected behavior")]
        string problem,
        [Description("Urgency level: 'critical', 'high', 'medium', or 'low'")]
        string urgency = "medium")
    {
        var urgencyGuidance = urgency.ToLower() switch
        {
            "critical" => "Focus on immediate fixes and workarounds to get the definition working quickly.",
            "high" => "Provide both quick fixes and thorough analysis to prevent recurrence.",
            "low" => "Conduct comprehensive analysis including optimization opportunities and best practices.",
            _ => "Balance immediate solutions with preventive measures and learning opportunities."
        };

        return new ChatMessage(ChatRole.User,
            $"""
             TROUBLESHOOT GRASSHOPPER ISSUE: {problem}

             URGENCY LEVEL: {urgency} - {urgencyGuidance}

             ## SYSTEMATIC DIAGNOSTIC APPROACH:

             ### 1. IMMEDIATE ASSESSMENT
             - Reproduce the issue and document exact conditions
             - Identify error messages, warnings, or unexpected outputs
             - Determine scope of impact (single component vs. entire definition)
             - Assess if this is a new issue or recurring problem

             ### 2. ROOT CAUSE ANALYSIS
             Investigate these common Grasshopper problem categories:

             **Data Flow Issues:**
             - Data type mismatches (numbers vs. geometry vs. text)
             - List structure problems (item access, list lengths)
             - Data tree branch/path inconsistencies
             - Null or empty data propagation

             **Component Problems:**
             - Parameter configuration errors
             - Input/output connection issues
             - Component version compatibility
             - Missing or corrupted plug-ins

             **Performance Issues:**
             - Infinite loops or recursive calculations
             - Memory exhaustion from large datasets
             - Heavy computational components blocking UI
             - Inefficient data tree operations

             **Geometric Issues:**
             - Tolerance and precision problems
             - Invalid or degenerate geometry
             - Coordinate system misalignment
             - Scale or unit discrepancies

             **Scripting Problems:**
             - Python/C# runtime errors and exceptions
             - Import/library compatibility issues
             - Variable scope and state management
             - Integration with Grasshopper data model
             - C# reserved keyword conflicts (avoid 'out', 'in', 'ref', 'params')
             - Parameter name case sensitivity issues
             - Script compilation failures and syntax errors

             ### 3. DIAGNOSTIC TOOLS & TECHNIQUES
             - Use `GetComponentInfo()` to inspect problematic components
             - Use `GetComponentRuntimeInfo()` for detailed error checking and diagnostics
             - Implement data validation points throughout the definition
             - Add temporary visualization components to trace data flow
             - Isolate sections by disconnecting downstream components
             - Create minimal test cases to reproduce the issue
             - For C# scripts: check parameter names, reserved keywords, and compilation errors

             ### 4. SOLUTION STRATEGIES
             Provide multiple solution approaches:

             **Quick Fixes:**
             - Immediate workarounds using available MCP tools
             - Parameter adjustments to bypass the issue
             - Component substitutions or reconfigurations

             **Comprehensive Solutions:**
             - Address underlying architectural problems
             - Implement robust error handling and validation
             - Optimize data flow for reliability and performance

             **Prevention Measures:**
             - Design patterns to avoid similar issues
             - Testing strategies for complex definitions
             - Documentation and commenting best practices

             ### 5. IMPLEMENTATION GUIDANCE
             Provide step-by-step instructions using MCP tools:
             - `GetComponentInfo()` for detailed component analysis
             - `GetComponentRuntimeInfo()` for comprehensive error checking and diagnostics
             - `SetComponentValue()` for parameter corrections
             - `ConnectComponents()` for rewiring solutions (indices are 0-based)
             - `AddPythonScriptComponent()` or `AddCSharpScriptComponent()` for custom fixes
             - `ModifyScriptComponentParameters()` for C# script parameter configuration
             - `SetComponentScript()` for updating script content

             ### 6. VERIFICATION & TESTING
             - Test cases to confirm the fix works correctly
             - Edge case scenarios to ensure robustness
             - Performance validation for optimization fixes
             - Regression testing for related functionality

             ### 7. LEARNING OUTCOMES
             - Explanation of why the problem occurred
             - Best practices to prevent similar issues
             - Advanced techniques revealed through troubleshooting
             - Resources for deeper understanding

             Include specific error codes, component names, and parameter values where relevant. Prioritize solutions by effectiveness and implementation complexity.
             """);
    }

    /// <summary>
    /// Creates a comprehensive educational prompt for learning Grasshopper concepts and techniques.
    /// </summary>
    [McpServerPrompt]
    [Description("Creates a structured learning prompt for mastering Grasshopper concepts with hands-on examples and progressive skill building")]
    public static ChatMessage LearnGrasshopperConcept(
        [Description("Grasshopper concept, technique, or skill to learn about")]
        string concept,
        [Description("Learning style preference: 'visual', 'hands-on', 'theoretical', or 'mixed'")]
        string learningStyle = "mixed",
        [Description("Current skill level: 'beginner', 'intermediate', or 'advanced'")]
        string skillLevel = "beginner")
    {
        var styleGuidance = learningStyle.ToLower() switch
        {
            "visual" => "Emphasize diagrams, visual examples, and canvas layout illustrations.",
            "hands-on" => "Focus on practical exercises, step-by-step tutorials, and immediate application.",
            "theoretical" => "Provide in-depth explanations of underlying principles and mathematical foundations.",
            _ => "Balance theory, visual examples, and practical exercises for comprehensive understanding."
        };

        var levelGuidance = skillLevel.ToLower() switch
        {
            "intermediate" => "Build on basic knowledge with more complex examples and interconnected concepts.",
            "advanced" => "Explore edge cases, optimization techniques, and expert-level applications.",
            _ => "Start with fundamentals and gradually introduce more complex ideas."
        };

        return new ChatMessage(ChatRole.User,
            $"""
             GRASSHOPPER LEARNING SESSION: {concept}

             LEARNING STYLE: {learningStyle} - {styleGuidance}
             SKILL LEVEL: {skillLevel} - {levelGuidance}

             ## COMPREHENSIVE LEARNING FRAMEWORK:

             ### 1. CONCEPT FOUNDATION
             - Clear, precise definition of {concept}
             - Historical context and evolution in parametric design
             - Core principles and underlying theory
             - Relationship to broader computational design concepts

             ### 2. WHY IT MATTERS
             - Importance in parametric and algorithmic design
             - Real-world applications and industry use cases
             - Problem-solving capabilities this concept enables
             - Career and professional development relevance

             ### 3. PRACTICAL UNDERSTANDING
             **Component Ecosystem:**
             - Specific Grasshopper components that implement this concept
             - Parameter configurations and typical value ranges
             - Input/output relationships and data flow patterns
             - Compatible components and common combinations

             **Implementation Patterns:**
             - Standard workflows and methodologies
             - Best practices for different scenarios
             - Performance considerations and optimization strategies
             - Common variations and alternative approaches

             ### 4. HANDS-ON TUTORIALS
             Create progressive learning exercises using MCP tools:

             **Basic Exercise (15-30 minutes):**
             - Simple implementation demonstrating core concept
             - Step-by-step instructions with exact component placements
             - Expected outcomes and validation criteria

             **Intermediate Challenge (45-60 minutes):**
             - More complex scenario combining multiple concepts
             - Problem-solving approach and design thinking
             - Multiple solution pathways and trade-offs

             **Advanced Project (2+ hours):**
             - Real-world application or creative exploration
             - Integration with other advanced techniques
             - Optimization and refinement opportunities

             ### 5. VISUAL PROGRAMMING PRINCIPLES
             - How this concept fits into Grasshopper's visual paradigm
             - Canvas organization and workflow strategies
             - Data visualization and debugging techniques
             - Documentation and collaboration best practices

             ### 6. MATHEMATICAL & GEOMETRIC FOUNDATIONS
             - Underlying mathematical principles (when applicable)
             - Geometric relationships and spatial reasoning
             - Algorithmic thinking and computational logic
             - Cross-disciplinary connections (physics, biology, etc.)

             ### 7. COMMON PITFALLS & TROUBLESHOOTING
             - Typical mistakes beginners make
             - Warning signs and error patterns to watch for
             - Debugging strategies and diagnostic techniques
             - Recovery methods when things go wrong

             ### 8. ADVANCED TECHNIQUES & EXTENSIONS
             - Expert-level applications and edge cases
             - Integration with scripting (Python/C#)
             - Plugin ecosystems and third-party tools
             - Future developments and emerging trends

             ### 9. ASSESSMENT & PRACTICE
             - Self-evaluation criteria and learning milestones
             - Practice exercises with increasing difficulty
             - Portfolio project suggestions
             - Community resources and continued learning paths

             ### 10. ACTIONABLE NEXT STEPS
             - Immediate follow-up exercises to reinforce learning
             - Related concepts to explore next
             - Resources for deeper study
             - Ways to apply this knowledge in current projects

             For each practical example, provide complete implementation instructions using available MCP tools:
             `AddComponent()`, `SetComponentValue()`, `ConnectComponents()`, `AddPythonScriptComponent()`, `AddCSharpScriptComponent()`

             Make the content engaging, progressive, and immediately applicable to real design challenges.
             """);
    }

    /// <summary>
    /// Creates a prompt for analyzing and reverse-engineering existing Grasshopper definitions.
    /// </summary>
    [McpServerPrompt]
    [Description("Creates a systematic prompt for understanding, documenting, and learning from existing Grasshopper definitions")]
    public static ChatMessage AnalyzeGrasshopperDefinition(
        [Description("Description of the Grasshopper definition to analyze")]
        string definitionDescription,
        [Description("Analysis focus: 'learning', 'documentation', 'optimization', or 'replication'")]
        string analysisFocus = "learning")
    {
        var focusGuidance = analysisFocus.ToLower() switch
        {
            "documentation" => "Create comprehensive documentation for sharing and collaboration.",
            "optimization" => "Identify improvement opportunities and optimization potential.",
            "replication" => "Understand the definition well enough to recreate or adapt it.",
            _ => "Extract learning insights and understand design principles and techniques."
        };

        return new ChatMessage(ChatRole.User,
            $"""
             ANALYZE GRASSHOPPER DEFINITION: {definitionDescription}

             ANALYSIS FOCUS: {analysisFocus} - {focusGuidance}

             ## SYSTEMATIC ANALYSIS FRAMEWORK:

             ### 1. OVERVIEW & PURPOSE
             - Primary function and intended outcomes
             - Design intent and creative vision
             - Target users and use cases
             - Technical requirements and constraints

             ### 2. ARCHITECTURAL ANALYSIS
             - Overall data flow and logical structure
             - Component organization and grouping patterns
             - Input/output relationships and dependencies
             - Modular components and reusable sections

             ### 3. COMPONENT INVENTORY
             - Complete list of components used
             - Purpose and role of each component
             - Parameter configurations and critical values
             - Custom scripts and their functionality

             ### 4. DESIGN PATTERNS & TECHNIQUES
             - Algorithmic approaches and methodologies
             - Advanced techniques and expert-level implementations
             - Creative solutions and innovative applications
             - Efficiency strategies and optimization patterns

             ### 5. LEARNING EXTRACTION
             - Key concepts and principles demonstrated
             - Transferable techniques and methodologies
             - Best practices and professional approaches
             - Problem-solving strategies and design thinking

             ### 6. STEP-BY-STEP RECONSTRUCTION
             Provide instructions to recreate using MCP tools:
             - Component placement strategy and canvas layout
             - Connection sequences and parameter settings
             - Critical values and configuration details
             - Testing and validation checkpoints

             ### 7. VARIATIONS & ADAPTATIONS
             - Parameter ranges and customization options
             - Alternative approaches for similar outcomes
             - Scalability and modification potential
             - Integration possibilities with other systems

             ### 8. DOCUMENTATION & KNOWLEDGE CAPTURE
             - Clear explanations for each major section
             - Rationale behind design decisions
             - Performance characteristics and limitations
             - Maintenance and update considerations

             Provide actionable insights that enable understanding, learning, and practical application of the analyzed definition.
             """);
    }

}
