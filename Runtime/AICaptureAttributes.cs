using System;

namespace ChromeHypercube.AICapture
{
    /// <summary>
    /// Marks a method as an action a scenario can call (InvokeAction / SetLocale steps).
    /// Supported signatures: void M(), void M(string[] args), IEnumerator M(), IEnumerator M(string[] args).
    /// Can be static, or an instance method on a MonoBehaviour that is active in the scene.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CaptureActionAttribute : Attribute
    {
        public string ActionId { get; }
        public CaptureActionAttribute(string actionId)
        {
            ActionId = actionId;
        }
    }

    /// <summary>
    /// Marks a method as a condition for the WaitUntil step.
    /// Supported signatures: bool M() or bool M(string[] args).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CaptureConditionAttribute : Attribute
    {
        public string ConditionId { get; }
        public CaptureConditionAttribute(string conditionId)
        {
            ConditionId = conditionId;
        }
    }
}
