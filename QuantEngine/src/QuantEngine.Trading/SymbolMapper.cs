// SymbolMapper now lives in QuantEngine.Domain.Utilities to avoid
// Infrastructure → Trading → Infrastructure circular dependency.
// This file re-exports it so existing code in the Trading namespace still compiles.
global using QuantEngine.Domain.Utilities;
