using System;

namespace Blade;

/// <summary>
/// Suppresses BLD0002 on a method or constructor that is intentionally part of
/// the public API surface and may be called by code outside the Blade assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
public sealed class PublicApiAttribute : Attribute { }
