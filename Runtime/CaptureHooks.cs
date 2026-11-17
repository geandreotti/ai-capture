using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Finds methods marked with [CaptureAction] / [CaptureCondition] and invokes them.
    /// </summary>
    public static class CaptureHooks
    {
        private static Dictionary<string, MethodInfo> _actions;
        private static Dictionary<string, MethodInfo> _conditions;

        private static readonly string[] SkippedAssemblyPrefixes =
        {
            "System", "mscorlib", "netstandard", "Mono.", "UnityEngine", "UnityEditor", "Unity.", "nunit", "Newtonsoft"
        };

        // Domain reload off: drop the cache so hooks are rescanned for each Play session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() { _actions = null; _conditions = null; }

        public static IReadOnlyCollection<string> ActionIds { get { EnsureLoaded(); return _actions.Keys; } }
        public static IReadOnlyCollection<string> ConditionIds { get { EnsureLoaded(); return _conditions.Keys; } }

        public static void Refresh()
        {
            _actions = new Dictionary<string, MethodInfo>();
            _conditions = new Dictionary<string, MethodInfo>();

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (SkippedAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var type in types)
                {
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(flags); }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        var action = m.GetCustomAttribute<CaptureActionAttribute>();
                        if (action != null) Register(_actions, action.ActionId, m, "CaptureAction");

                        var condition = m.GetCustomAttribute<CaptureConditionAttribute>();
                        if (condition != null) Register(_conditions, condition.ConditionId, m, "CaptureCondition");
                    }
                }
            }
        }

        public static bool HasAction(string id) { EnsureLoaded(); return !string.IsNullOrEmpty(id) && _actions.ContainsKey(id); }
        public static bool HasCondition(string id) { EnsureLoaded(); return !string.IsNullOrEmpty(id) && _conditions.ContainsKey(id); }

        /// <summary>Invokes an action. Returns the method's return value (may be an IEnumerator).</summary>
        public static object InvokeAction(string id, string[] args)
        {
            EnsureLoaded();
            if (!_actions.TryGetValue(id ?? "", out var m))
                throw new KeyNotFoundException($"No method with [CaptureAction(\"{id}\")] was found.");
            return Invoke(m, args);
        }

        public static bool EvaluateCondition(string id, string[] args)
        {
            EnsureLoaded();
            if (!_conditions.TryGetValue(id ?? "", out var m))
                throw new KeyNotFoundException($"No method with [CaptureCondition(\"{id}\")] was found.");
            return (bool)Invoke(m, args);
        }

        private static void EnsureLoaded()
        {
            if (_actions == null || _conditions == null) Refresh();
        }

        private static void Register(Dictionary<string, MethodInfo> dict, string id, MethodInfo m, string kind)
        {
            if (string.IsNullOrEmpty(id)) return;

            var ps = m.GetParameters();
            bool validParams = ps.Length == 0 || (ps.Length == 1 && ps[0].ParameterType == typeof(string[]));
            if (!validParams)
            {
                Debug.LogWarning($"[AICapture] {kind} \"{id}\" em {m.DeclaringType?.Name}.{m.Name} ignored: use () or (string[] args).");
                return;
            }
            if (kind == "CaptureCondition" && m.ReturnType != typeof(bool))
            {
                Debug.LogWarning($"[AICapture] CaptureCondition \"{id}\" em {m.DeclaringType?.Name}.{m.Name} must return bool.");
                return;
            }
            if (!m.IsStatic && !typeof(Component).IsAssignableFrom(m.DeclaringType))
            {
                Debug.LogWarning($"[AICapture] {kind} \"{id}\" em {m.DeclaringType?.Name}.{m.Name}: instance methods only work on MonoBehaviours.");
                return;
            }
            if (dict.ContainsKey(id))
            {
                Debug.LogWarning($"[AICapture] {kind} \"{id}\" is duplicated ({m.DeclaringType?.Name}.{m.Name}); keeping the first one.");
                return;
            }
            dict[id] = m;
        }

        private static object Invoke(MethodInfo m, string[] args)
        {
            object target = null;
            if (!m.IsStatic)
            {
                target = UnityEngine.Object.FindFirstObjectByType(m.DeclaringType);
                if (target == null)
                    throw new InvalidOperationException(
                        $"No active {m.DeclaringType?.Name} in the scene to call {m.Name}.");
            }

            object[] callArgs = m.GetParameters().Length == 0
                ? null
                : new object[] { args ?? Array.Empty<string>() };

            try
            {
                return m.Invoke(target, callArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }
    }
}
