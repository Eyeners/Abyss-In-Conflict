using System;
using System.Collections.Generic;

namespace AIC.Components
{
    // Each caller owns its list until Return, including nested callbacks. Lists
    // never enter the pool while they are still being enumerated by a consumer.
    internal static class ScratchLists<T>
    {
        private const int MaxRetained = 32, MaxCapacity = 4096;
        [ThreadStatic] private static Stack<List<T>> available;
        internal static List<T> Rent()
            => available != null && available.Count > 0 ? available.Pop() : new List<T>();
        internal static void Return(List<T> list)
        {
            if (list == null) return;
            list.Clear(); // Release entities even when an unusually large buffer is discarded.
            if (list.Capacity > MaxCapacity) return;
            available ??= new Stack<List<T>>();
            if (available.Count < MaxRetained) available.Push(list);
        }
    }
}
