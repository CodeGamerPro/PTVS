﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.PythonTools.Analysis.Analyzer;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Values {
    class TypingTypeInfo : AnalysisValue {
        private readonly string _baseName;
        private readonly IReadOnlyList<IAnalysisSet> _args;

        public TypingTypeInfo(string baseName) {
            _baseName = baseName;
        }

        private TypingTypeInfo(string baseName, IReadOnlyList<IAnalysisSet> args) {
            _baseName = baseName;
            _args = args;
        }

        public TypingTypeInfo MakeGeneric(IReadOnlyList<IAnalysisSet> args) {
            if (_args == null) {
                return new TypingTypeInfo(_baseName, args);
            }
            return this;
        }

        public IAnalysisSet Finalize(ExpressionEvaluator eval, Node node, AnalysisUnit unit) {
            if (Push()) {
                try {
                    var finalizer = new TypingTypeInfoFinalizer(eval, node, unit);
                    return finalizer.Finalize(_baseName, _args)
                        ?? finalizer.Finalize(_baseName)
                        ?? AnalysisSet.Empty;
                } finally {
                    Pop();
                }
            }
            return AnalysisSet.Empty;
        }

        public override string ToString() {
            if (_args != null) {
                return $"<Typing:{_baseName}[{string.Join(", ", _args)}]>";
            }
            return $"<Typing:{_baseName}>";
        }

        public IReadOnlyList<IAnalysisSet> ToTypeList() {
            if (_baseName == " List") {
                return _args;
            }
            return null;
        }
    }

    sealed class TypingTypeInfoFinalizer {
        private readonly ExpressionEvaluator _eval;
        private readonly Node _node;
        private readonly AnalysisUnit _unit;

        public TypingTypeInfoFinalizer(ExpressionEvaluator eval, Node node, AnalysisUnit unit) {
            _eval = eval;
            _node = node;
            _unit = unit;
        }

        private InterpreterScope Scope => _eval.Scope;
        private PythonAnalyzer State => _unit.ProjectState;
        private IKnownPythonTypes Types => State.Types;
        private IKnownClasses ClassInfo => State.ClassInfos;
        private AnalysisValue NoneType => ClassInfo[BuiltinTypeId.NoneType];
        private AnalysisValue None => State._noneInst;
        private ProjectEntry Entry => _unit.ProjectEntry;


        private static IReadOnlyList<IAnalysisSet> GetTypeList(IAnalysisSet item) {
            return item.OfType<TypingTypeInfo>().FirstOrDefault()?.ToTypeList();
        }

        private SequenceBuiltinClassInfo GetSequenceType(string name) {
            switch (name) {
                case "List":
                case "Container":
                case "MutableSequence":
                    return ClassInfo[BuiltinTypeId.List] as SequenceBuiltinClassInfo;
                case "Tuple":
                case "Sequence":
                    return ClassInfo[BuiltinTypeId.Tuple] as SequenceBuiltinClassInfo;
            }

            return null;
        }

        private BuiltinClassInfo GetSetType(string name) {
            switch (name) {
                case "MutableSet":
                case "Set":
                    return ClassInfo[BuiltinTypeId.Set];
                case "FrozenSet":
                    return ClassInfo[BuiltinTypeId.FrozenSet];
            }

            return null;
        }

        public IAnalysisSet Finalize(string name, IReadOnlyList<IAnalysisSet> args) {
            if (string.IsNullOrEmpty(name) || args == null || args.Count == 0) {
                return null;
            }

            switch (name) {
                case "Union":
                    return AnalysisSet.UnionAll(args.Select(a => Finalize(a)));
                case "Optional":
                    return Finalize(args[0]).Add(NoneType);
                case "List":
                case "Tuple":
                case "Container":
                case "MutableSequence":
                case "Sequence":
                    try {
                        return Scope.GetOrMakeNodeValue(_node, NodeValueKind.Sequence, n => {
                            var t = GetSequenceType(name);
                            if (t == null) {
                                throw new KeyNotFoundException(name);
                            }
                            var seq = t.MakeFromIndexes(n, Entry);
                            seq.AddTypes(_unit, args.Select(ToInstance).ToArray());
                            return seq;
                        });
                    } catch (KeyNotFoundException) {
                        return null;
                    }
                case "KeysView":
                    if (args.Count < 1) {
                        return null;
                    }
                    return (Scope.GetOrMakeNodeValue(_node, NodeValueKind.DictLiteral, n => {
                        var di = new DictionaryInfo(Entry, n);
                        di.AddTypes(
                            n,
                            _unit,
                            ToInstance(args[0]),
                            None
                        );
                        return di;
                    }) as DictionaryInfo)?.GetKeysView(_unit);
                case "ValuesView":
                    if (args.Count < 1) {
                        return null;
                    }
                    return (Scope.GetOrMakeNodeValue(_node, NodeValueKind.DictLiteral, n => {
                        var di = new DictionaryInfo(Entry, n);
                        di.AddTypes(
                            n,
                            _unit,
                            None,
                            ToInstance(args[0])
                        );
                        return di;
                    }) as DictionaryInfo)?.GetValuesView(_unit);

                case "MutableSet":
                case "Set":
                case "FrozenSet":
                    try {
                        return Scope.GetOrMakeNodeValue(_node, NodeValueKind.Set, n => {
                            return new SetInfo(
                                GetSetType(name) ?? throw new KeyNotFoundException(name),
                                n,
                                Entry,
                                args.Select(ToVariableDef).ToArray()
                            );
                        });
                    } catch (KeyNotFoundException) {
                        return null;
                    }
                case "Mapping":
                case "MappingView":
                case "MutableMapping":
                case "Dict":
                case "ItemsView":
                    try {
                        if (args.Count < 2) {
                            return null;
                        }
                        var d = Scope.GetOrMakeNodeValue(_node, NodeValueKind.DictLiteral, n => {
                            var di = new DictionaryInfo(Entry, n);
                            di.AddTypes(n, _unit, ToInstance(args[0]), ToInstance(args[1]));
                            return di;
                        });
                        if (name == "ItemsView") {
                            return (d as DictionaryInfo).GetItemsView(_unit);
                        }
                        return d;
                    } catch (KeyNotFoundException) {
                        return null;
                    }

                case "Callable":
                    if (args.Count == 0) {
                        return null;
                    }
                    return Scope.GetOrMakeNodeValue(_node, NodeValueKind.None, n => {
                        var p = new ProtocolInfo(_unit.ProjectEntry);
                        p.AddReference(n, _unit);
                        var callArgs = GetTypeList(args[0]) ?? new[] { args[0] };
                        p.AddProtocol(new CallableProtocol(
                            p,
                            null,
                            callArgs,
                            ToInstance(args.ElementAtOrDefault(1) ?? AnalysisSet.Empty)
                        ));
                        return p;
                    });


                case "Iterable":
                case "Iterator": {
                        var iter = Scope.GetOrMakeNodeValue(_node, NodeValueKind.Iterator, n => {
                            var p = new ProtocolInfo(_unit.ProjectEntry);
                            p.AddReference(n, _unit);
                            p.AddProtocol(new IterableProtocol(p, AnalysisSet.UnionAll(args.Select(ToInstance))));
                            return p;
                        });
                        return (name == "Iterator") ? iter.GetIterator(_node, _unit) : iter;
                    }

                case "Generator":
                    return Scope.GetOrMakeNodeValue(_node, NodeValueKind.Iterator, n => {
                        var gi = new GeneratorInfo(State, Entry);
                        if (args.Count >= 1) {
                            gi.AddYield(n, _unit, ToInstance(args[0]), false);
                        }
                        if (args.Count >= 2) {
                            gi.AddSend(n, _unit, ToInstance(args[1]), false);
                        }
                        if (args.Count >= 3) {
                            gi.AddReturn(n, _unit, ToInstance(args[2]), false);
                        }
                        return gi;
                    });
                case "NamedTuple": return null;
            }

            return null;
        }

        public IAnalysisSet Finalize(string name) {
            if (string.IsNullOrEmpty(name)) {
                return null;
            }

            switch (name) {
                case "Callable": return ClassInfo[BuiltinTypeId.Function];
                case "Tuple": return ClassInfo[BuiltinTypeId.Tuple];
                case "Container": return ClassInfo[BuiltinTypeId.List];
                case "ItemsView": return ClassInfo[BuiltinTypeId.DictItems];
                case "Iterable": {
                        var p = new ProtocolInfo(Entry);
                        p.AddReference(_node, _unit);
                        p.AddProtocol(new IterableProtocol(p, AnalysisSet.Empty));
                        return p;
                    }
                case "Iterator": {
                        var p = new ProtocolInfo(Entry);
                        p.AddReference(_node, _unit);
                        p.AddProtocol(new IteratorProtocol(p, AnalysisSet.Empty));
                        return p;
                    }
                case "KeysView": return ClassInfo[BuiltinTypeId.DictKeys];
                case "Mapping": return ClassInfo[BuiltinTypeId.Dict];
                case "MappingView": return ClassInfo[BuiltinTypeId.Dict];
                case "MutableMapping": return ClassInfo[BuiltinTypeId.Dict];
                case "MutableSequence": return ClassInfo[BuiltinTypeId.List];
                case "MutableSet": return ClassInfo[BuiltinTypeId.Set];
                case "Sequence": return ClassInfo[BuiltinTypeId.List];
                case "ValuesView": return ClassInfo[BuiltinTypeId.DictValues];
                case "Dict": return ClassInfo[BuiltinTypeId.Dict];
                case "List": return ClassInfo[BuiltinTypeId.List];
                case "Set": return ClassInfo[BuiltinTypeId.Set];
                case "FrozenSet": return ClassInfo[BuiltinTypeId.FrozenSet];
                case "NamedTuple": return ClassInfo[BuiltinTypeId.Tuple];
                case "Generator": return ClassInfo[BuiltinTypeId.Generator];
                case "NoReturn": return AnalysisSet.Empty;
                case " List": return null;
            }

            return null;
        }

        private IAnalysisSet Finalize(IAnalysisSet set) {
            if (set.Split(out IReadOnlyList<TypingTypeInfo> typeInfo, out var rest)) {
                return rest.UnionAll(
                    typeInfo.Select(t => t.Finalize(_eval, _node, _unit))
                );
            }
            return set;
        }

        private VariableDef ToVariableDef(IAnalysisSet set) {
            var v = new VariableDef();
            v.AddTypes(_unit, ToInstance(set), enqueue: false, declaringScope: Entry);
            return v;
        }

        private IAnalysisSet ToInstance(IAnalysisSet set) {
            return Finalize(set).GetInstanceType();
        }

    }
}
