// The order of the checks on every tool call (spec MCP-020). ToolRegistry.CallTool hands in one function per check and runs
// them through here, so the order lives in one place and the unit tests can check it without TwinCAT or the IDE.
//
//   gates        steps 2-5: a refusal returns its text to the agent (isError) and the call ends there
//   before tool  steps 6-10: a check refuses by throwing; it ends the call with "Error: ..." like the tool itself would
//
// Step 1 (module switched off) happens in ToolRegistry.Call before CallTool; steps 11-13 (the tool, workflow observation,
// lessons, audit) follow in CallTool.
//
// SPDX-License-Identifier: MIT
namespace TcAgent;

public enum CallCheck
{
    Advisor,          // 2  an Advisor run changes nothing (ADV-002)
    NewCode,          // 3  new function outside a workflow: the specification question (WFL-020)
    SpecOpen,         // 4  code-writing call while a workflow's spec-ok step is open (WFL-012)
    CodingRules,      // 5  first code-writing call and the project has coding rules (WFL-030)
    Policy,           // 6  mode, TwinSAFE, target system (GRD-010, GRD-050, GRD-021)
    Arguments,        // 7  argument checks that would refuse anyway (GRD-034)
    ActivateGate,     // 8  tc_activate: build with 0 errors first (WFL-001; TCAGENT_ACTIVATE_GATE=0 switches it off)
    Confirm,          // 9  confirmation (GRD-030)
    KnowledgeBackup,  // 10 backup of the knowledge levels before a knowledge write (KNW-060)
}

public static class CallOrder
{
    /// <summary>The checks whose refusal is returned as text (MCP-020 steps 2-5), in order.</summary>
    public static readonly IReadOnlyList<CallCheck> Gates =
        new[] { CallCheck.Advisor, CallCheck.NewCode, CallCheck.SpecOpen, CallCheck.CodingRules };

    /// <summary>The checks right before the tool that refuse by throwing (MCP-020 steps 6-10), in order.</summary>
    public static readonly IReadOnlyList<CallCheck> BeforeTool =
        new[] { CallCheck.Policy, CallCheck.Arguments, CallCheck.ActivateGate, CallCheck.Confirm, CallCheck.KnowledgeBackup };

    /// <summary>Runs the gates in order and stops at the first one that refuses (returns a text); the later ones are not run.
    /// A gate without a function is skipped. Null when every gate let the call through.</summary>
    public static (CallCheck Check, string Text)? FirstRefusal(IReadOnlyDictionary<CallCheck, Func<string>> gates)
    {
        foreach (var c in Gates)
            if (gates.TryGetValue(c, out var f) && f() is { } text) return (c, text);
        return null;
    }

    /// <summary>Runs the checks before the tool in order; a refusal is an exception and ends the run there.
    /// A check without a function is skipped.</summary>
    public static void RunBeforeTool(IReadOnlyDictionary<CallCheck, Action> checks)
    {
        foreach (var c in BeforeTool)
            if (checks.TryGetValue(c, out var a)) a();
    }
}
