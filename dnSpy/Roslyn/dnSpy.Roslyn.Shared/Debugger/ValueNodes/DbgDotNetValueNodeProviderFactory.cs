﻿/*
    Copyright (C) 2014-2018 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Text;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Text;
using dnSpy.Debugger.DotNet.Metadata;
using dnSpy.Roslyn.Shared.Properties;

namespace dnSpy.Roslyn.Shared.Debugger.ValueNodes {
	abstract class DbgDotNetValueNodeProviderFactory {
		[Flags]
		enum TypeStateFlags {
			None				= 0,
			IsNullable			= 1,
			IsTupleType			= 2,
			IsDynamicViewType	= 4,
		}

		sealed class TypeState {
			public readonly DmdType Type;
			public readonly DmdType EnumerableType;
			public readonly TypeStateFlags Flags;
			public readonly string TypeExpression;
			public readonly bool HasNoChildren;
			public readonly MemberValueNodeInfoCollection InstanceMembers;
			public readonly MemberValueNodeInfoCollection StaticMembers;

			// Only if it's a tuple
			public readonly TupleField[] TupleFields;

			public bool IsNullable => (Flags & TypeStateFlags.IsNullable) != 0;
			public bool IsTupleType => (Flags & TypeStateFlags.IsTupleType) != 0;
			public bool IsDynamicViewType => (Flags & TypeStateFlags.IsDynamicViewType) != 0;

			public DbgValueNodeEvaluationOptions CachedEvalOptions;
			public MemberValueNodeInfoCollection CachedInstanceMembers;
			public MemberValueNodeInfoCollection CachedStaticMembers;

			public TypeState(DmdType type, string typeExpression) {
				Type = type;
				Flags = TypeStateFlags.None;
				TypeExpression = typeExpression;
				HasNoChildren = true;
				InstanceMembers = MemberValueNodeInfoCollection.Empty;
				StaticMembers = MemberValueNodeInfoCollection.Empty;
				TupleFields = Array.Empty<TupleField>();
			}

			public TypeState(DmdType type, string typeExpression, in MemberValueNodeInfoCollection instanceMembers, in MemberValueNodeInfoCollection staticMembers, TupleField[] tupleFields) {
				Type = type;
				EnumerableType = GetEnumerableType(type);
				Flags = GetFlags(type, tupleFields);
				TypeExpression = typeExpression;
				HasNoChildren = false;
				InstanceMembers = instanceMembers;
				StaticMembers = staticMembers;
				TupleFields = tupleFields;
			}

			static DmdType GetEnumerableType(DmdType type) {
				if (type.IsArray || type == type.AppDomain.System_String)
					return null;

				bool foundEnumerableType = false;
				var enumerableType = type.AppDomain.System_Collections_IEnumerable;
				var enumerableOfTType = type.AppDomain.System_Collections_Generic_IEnumerable_T;
				switch (GetEnumerableTypeKind(type, enumerableType, enumerableOfTType)) {
				case EnumerableTypeKind.Enumerable:
					foundEnumerableType = true;
					break;
				case EnumerableTypeKind.EnumerableOfT:
					return type;
				}

				foreach (var iface in type.GetInterfaces()) {
					switch (GetEnumerableTypeKind(iface, enumerableType, enumerableOfTType)) {
					case EnumerableTypeKind.Enumerable:
						foundEnumerableType = true;
						break;
					case EnumerableTypeKind.EnumerableOfT:
						return iface;
					}
				}
				return foundEnumerableType ? enumerableType : null;
			}

			enum EnumerableTypeKind {
				None,
				Enumerable,
				EnumerableOfT,
			}

			static EnumerableTypeKind GetEnumerableTypeKind(DmdType type, DmdType enumerableType, DmdType enumerableOfTType) {
				if (type == enumerableType)
					return EnumerableTypeKind.Enumerable;
				if (type.IsConstructedGenericType && type.GetGenericArguments().Count == 1) {
					if (type.GetGenericTypeDefinition() == enumerableOfTType)
						return EnumerableTypeKind.EnumerableOfT;
				}
				return EnumerableTypeKind.None;
			}

			static TypeStateFlags GetFlags(DmdType type, TupleField[] tupleFields) {
				var res = TypeStateFlags.None;
				if (type.IsNullable)
					res |= TypeStateFlags.IsNullable;
				if (tupleFields.Length != 0)
					res |= TypeStateFlags.IsTupleType;
				if (CheckIsDynamicViewType(type))
					res |= TypeStateFlags.IsDynamicViewType;
				return res;
			}

			static bool CheckIsDynamicViewType(DmdType type) {
				// Windows Runtime types aren't supported
				if (type.IsWindowsRuntime)
					return false;

				// Microsoft.CSharp.RuntimeBinder.DynamicMetaObjectProviderDebugView supports COM objects
				if (type.CanCastTo(type.AppDomain.GetWellKnownType(DmdWellKnownType.System___ComObject)))
					return true;

				// Microsoft.CSharp.RuntimeBinder.DynamicMetaObjectProviderDebugView supports IDynamicMetaObjectProvider.
				// This type is defined in Microsoft.CSharp which isn't always loaded. That's the reason we don't
				// use a DmdWellKnownType, since when searching for one, the code will check every loaded assembly
				// until the type is found, forcing all lazy-loaded metadata to be loaded. Most of the time it would
				// fail, and thus load all metadata. It's a problem when debugging programs with 100+ loaded assemblies.
				foreach (var iface in type.GetInterfaces()) {
					if ((object)iface.DeclaringType == null && iface.MetadataNamespace == "System.Dynamic" && iface.MetadataName == "IDynamicMetaObjectProvider")
						return true;
				}

				return false;
			}
		}

		readonly LanguageValueNodeFactory valueNodeFactory;
		readonly StringComparer stringComparer;

		protected DbgDotNetValueNodeProviderFactory(LanguageValueNodeFactory valueNodeFactory, bool isCaseSensitive) {
			this.valueNodeFactory = valueNodeFactory ?? throw new ArgumentNullException(nameof(valueNodeFactory));
			stringComparer = isCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
		}

		/// <summary>
		/// Returns true if <paramref name="type"/> is a primitive type that doesn't show any members,
		/// eg. integers, booleans, floating point numbers, strings
		/// </summary>
		/// <param name="type">Type to check</param>
		/// <returns></returns>
		protected abstract bool HasNoChildren(DmdType type);

		protected abstract DbgDotNetText InstanceMembersName { get; }
		protected abstract DbgDotNetText StaticMembersName { get; }
		protected abstract void FormatTypeName(ITextColorWriter output, DmdType type);
		protected abstract void FormatFieldName(ITextColorWriter output, DmdFieldInfo field);
		protected abstract void FormatPropertyName(ITextColorWriter output, DmdPropertyInfo property);
		public abstract void FormatArrayName(ITextColorWriter output, int index);
		public abstract void FormatArrayName(ITextColorWriter output, int[] indexes);
		public abstract string GetNewObjectExpression(DmdConstructorInfo ctor, string argumentExpression, DmdType expectedType);
		public abstract string GetCallExpression(DmdMethodBase method, string instanceExpression);
		public abstract string GetDereferenceExpression(string instanceExpression);
		public abstract ref readonly DbgDotNetText GetDereferencedName();

		internal void FormatTypeName2(ITextColorWriter output, DmdType type) => FormatTypeName(output, type);

		[Flags]
		enum CreationOptions {
			None			= 0,
			NoNullable		= 1,
			NoProxy			= 2,
		}

		public DbgDotNetValueNodeProviderResult Create(DbgEvaluationInfo evalInfo, bool addParens, DmdType slotType, DbgDotNetValueNodeInfo nodeInfo, DbgValueNodeEvaluationOptions options) {
			var providers = new List<DbgDotNetValueNodeProvider>(2);
			Create(evalInfo, providers, addParens, slotType, nodeInfo, options, CreationOptions.None);
			return new DbgDotNetValueNodeProviderResult(DbgDotNetValueNodeProvider.Create(providers));
		}

		public DbgDotNetValueNodeProviderResult CreateDynamicView(DbgEvaluationInfo evalInfo, bool addParens, DmdType slotType, DbgDotNetValueNodeInfo nodeInfo, DbgValueNodeEvaluationOptions options) {
			var state = GetTypeState(nodeInfo);
			var provider = TryCreateDynamicView(state, nodeInfo.Expression, nodeInfo.Value, slotType, options);
			if (provider != null)
				return new DbgDotNetValueNodeProviderResult(provider);
			return new DbgDotNetValueNodeProviderResult(dnSpy_Roslyn_Shared_Resources.DynamicView_MustBeDynamicOrComType);
		}

		public DbgDotNetValueNodeProviderResult CreateResultsView(DbgEvaluationInfo evalInfo, bool addParens, DmdType slotType, DbgDotNetValueNodeInfo nodeInfo, DbgValueNodeEvaluationOptions options) {
			var state = GetTypeState(nodeInfo);
			var provider = TryCreateResultsView(state, nodeInfo.Expression, nodeInfo.Value, slotType, options);
			if (provider != null)
				return new DbgDotNetValueNodeProviderResult(provider);
			return new DbgDotNetValueNodeProviderResult(dnSpy_Roslyn_Shared_Resources.ResultsView_MustBeEnumerableType);
		}

		TypeState GetTypeState(DbgDotNetValueNodeInfo nodeInfo) {
			var type = nodeInfo.Value.Type;
			if (type.IsByRef)
				type = type.GetElementType();
			return GetOrCreateTypeState(type);
		}

		void Create(DbgEvaluationInfo evalInfo, List<DbgDotNetValueNodeProvider> providers, bool addParens, DmdType slotType, DbgDotNetValueNodeInfo nodeInfo, DbgValueNodeEvaluationOptions options, CreationOptions createFlags) =>
			CreateCore(evalInfo, providers, addParens, slotType, nodeInfo, GetTypeState(nodeInfo), options, createFlags);

		TypeState GetOrCreateTypeState(DmdType type) {
			var state = StateWithKey<TypeState>.TryGet(type, this);
			if (state != null)
				return state;
			return CreateTypeState(type);

			TypeState CreateTypeState(DmdType type2) {
				var state2 = CreateTypeStateCore(type2);
				return StateWithKey<TypeState>.GetOrCreate(type2, this, () => state2);
			}
		}

		sealed class MemberValueNodeInfoEqualityComparer : IComparer<MemberValueNodeInfo> {
			public static readonly MemberValueNodeInfoEqualityComparer Instance = new MemberValueNodeInfoEqualityComparer();
			MemberValueNodeInfoEqualityComparer() { }

			public int Compare(MemberValueNodeInfo x, MemberValueNodeInfo y) {
				int c = GetOrder(x.Member.MemberType) - GetOrder(y.Member.MemberType);
				if (c != 0)
					return c;

				c = StringComparer.OrdinalIgnoreCase.Compare(x.Member.Name, y.Member.Name);
				if (c != 0)
					return c;

				c = y.InheritanceLevel.CompareTo(x.InheritanceLevel);
				if (c != 0)
					return c;
				return x.Member.MetadataToken - y.Member.MetadataToken;
			}

			static int GetOrder(DmdMemberTypes memberType) {
				if (memberType == DmdMemberTypes.Property)
					return 0;
				if (memberType == DmdMemberTypes.Field)
					return 1;
				throw new InvalidOperationException();
			}
		}

		string GetTypeExpression(DmdType type) {
			var output = new StringBuilderTextColorOutput();
			FormatTypeName(output, type);
			return output.ToString();
		}

		TupleField[] TryCreateTupleFields(DmdType type) {
			var tupleArity = Formatters.TypeFormatterUtils.GetTupleArity(type);
			if (tupleArity <= 0)
				return null;

			var tupleFields = new TupleField[tupleArity];
			foreach (var info in Formatters.TupleTypeUtils.GetTupleFields(type, tupleArity)) {
				if (info.tupleIndex < 0)
					return null;
				var defaultName = GetDefaultTupleName(info.tupleIndex);
				tupleFields[info.tupleIndex] = new TupleField(defaultName, info.fields.ToArray());
			}
			return tupleFields;
		}

		static string GetDefaultTupleName(int tupleIndex) => "Item" + (tupleIndex + 1).ToString();

		TypeState CreateTypeStateCore(DmdType type) {
			var typeExpression = GetTypeExpression(type);
			if (HasNoChildren(type) || type.IsFunctionPointer)
				return new TypeState(type, typeExpression);

			MemberValueNodeInfoCollection instanceMembers, staticMembers;
			TupleField[] tupleFields;

			Debug.Assert(!type.IsByRef);
			if (type.TypeSignatureKind == DmdTypeSignatureKind.Type || type.TypeSignatureKind == DmdTypeSignatureKind.GenericInstance) {
				tupleFields = TryCreateTupleFields(type) ?? Array.Empty<TupleField>();

				var instanceMembersList = new List<MemberValueNodeInfo>();
				var staticMembersList = new List<MemberValueNodeInfo>();
				bool instanceHasHideRoot = false;
				bool staticHasHideRoot = false;

				byte inheritanceLevel;
				DmdType currentType;

				inheritanceLevel = 0;
				currentType = type;
				foreach (var field in type.Fields) {
					var declType = field.DeclaringType;
					while (declType != currentType) {
						Debug.Assert((object)currentType.BaseType != null);
						currentType = currentType.BaseType;
						if (inheritanceLevel != byte.MaxValue)
							inheritanceLevel++;
					}

					var nodeInfo = new MemberValueNodeInfo(field, inheritanceLevel);
					if (field.IsStatic) {
						staticHasHideRoot |= nodeInfo.HasDebuggerBrowsableState_RootHidden;
						staticMembersList.Add(nodeInfo);
					}
					else {
						instanceHasHideRoot |= nodeInfo.HasDebuggerBrowsableState_RootHidden;
						instanceMembersList.Add(nodeInfo);
					}
				}

				inheritanceLevel = 0;
				currentType = type;
				foreach (var property in type.Properties) {
					if (property.GetMethodSignature().GetParameterTypes().Count != 0)
						continue;
					var declType = property.DeclaringType;
					while (declType != currentType) {
						Debug.Assert((object)currentType.BaseType != null);
						currentType = currentType.BaseType;
						if (inheritanceLevel != byte.MaxValue)
							inheritanceLevel++;
					}
					var getter = property.GetGetMethod(DmdGetAccessorOptions.All);
					if ((object)getter == null || getter.GetMethodSignature().GetParameterTypes().Count != 0)
						continue;
					var nodeInfo = new MemberValueNodeInfo(property, inheritanceLevel);
					if (getter.IsStatic) {
						staticHasHideRoot |= nodeInfo.HasDebuggerBrowsableState_RootHidden;
						staticMembersList.Add(nodeInfo);
					}
					else {
						instanceHasHideRoot |= nodeInfo.HasDebuggerBrowsableState_RootHidden;
						instanceMembersList.Add(nodeInfo);
					}
				}

				var instanceMembersArray = InitializeOverloadedMembers(instanceMembersList.ToArray());
				var staticMembersArray = InitializeOverloadedMembers(staticMembersList.ToArray());

				instanceMembers = instanceMembersList.Count == 0 ? MemberValueNodeInfoCollection.Empty : new MemberValueNodeInfoCollection(instanceMembersArray, instanceHasHideRoot);
				staticMembers = staticMembersList.Count == 0 ? MemberValueNodeInfoCollection.Empty : new MemberValueNodeInfoCollection(staticMembersArray, staticHasHideRoot);

				Array.Sort(instanceMembers.Members, MemberValueNodeInfoEqualityComparer.Instance);
				Array.Sort(staticMembers.Members, MemberValueNodeInfoEqualityComparer.Instance);
				var output = ObjectCache.AllocDotNetTextOutput();
				UpdateNames(instanceMembers.Members, output);
				UpdateNames(staticMembers.Members, output);
				ObjectCache.Free(ref output);
			}
			else {
				staticMembers = instanceMembers = MemberValueNodeInfoCollection.Empty;
				tupleFields = Array.Empty<TupleField>();
			}

			return new TypeState(type, typeExpression, instanceMembers, staticMembers, tupleFields);
		}

		MemberValueNodeInfo[] InitializeOverloadedMembers(MemberValueNodeInfo[] memberInfos) {
			var dict = new Dictionary<string, object>(stringComparer);
			for (int i = 0; i < memberInfos.Length; i++) {
				ref var info = ref memberInfos[i];
				if (dict.TryGetValue(info.Member.Name, out object value)) {
					List<int> list;
					if (value is int) {
						list = new List<int>(2);
						list.Add((int)value);
						dict[info.Member.Name] = list;
					}
					else
						list = (List<int>)value;
					list.Add(i);
				}
				else
					dict[info.Member.Name] = i;
			}
			var memberInfosTmp = memberInfos;
			foreach (var kv in dict) {
				var list = kv.Value as List<int>;
				if (list == null)
					continue;
				list.Sort((a, b) => {
					ref var ai = ref memberInfosTmp[a];
					ref var bi = ref memberInfosTmp[b];
					return ai.InheritanceLevel - bi.InheritanceLevel;
				});
				var firstType = memberInfos[list[0]].Member.DeclaringType;
				for (int i = 1; i < list.Count; i++) {
					ref var info = ref memberInfos[list[i]];
					if (info.Member.DeclaringType != firstType)
						info.SetNeedCastAndNeedTypeName();
				}
			}
			return memberInfos;
		}

		void UpdateNames(MemberValueNodeInfo[] infos, DbgDotNetTextOutput output) {
			if (infos.Length == 0)
				return;

			for (int i = 0; i < infos.Length; i++) {
				ref var info = ref infos[i];
				FormatName(output, info.Member);
				if (info.NeedTypeName) {
					output.Write(BoxedTextColor.Text, " ");
					output.Write(BoxedTextColor.Punctuation, "(");
					FormatTypeName(output, info.Member.DeclaringType);
					output.Write(BoxedTextColor.Punctuation, ")");
				}
				info.Name = output.CreateAndReset();
			}
		}

		void FormatName(DbgDotNetTextOutput output, DmdMemberInfo member) {
			if (member.MemberType == DmdMemberTypes.Field)
				FormatFieldName(output, (DmdFieldInfo)member);
			else {
				Debug.Assert(member.MemberType == DmdMemberTypes.Property);
				FormatPropertyName(output, (DmdPropertyInfo)member);
			}
		}

		bool TryCreateNullable(DbgEvaluationInfo evalInfo, List<DbgDotNetValueNodeProvider> providers, bool addParens, DmdType slotType, DbgDotNetValueNodeInfo nodeInfo, TypeState state, DbgValueNodeEvaluationOptions evalOptions, CreationOptions createFlags) {
			Debug.Assert((createFlags & CreationOptions.NoNullable) == 0);
			if (!state.IsNullable)
				return false;

			var fields = Formatters.NullableTypeUtils.TryGetNullableFields(state.Type);
			Debug.Assert((object)fields.hasValueField != null);
			if ((object)fields.hasValueField == null)
				return false;

			var runtime = evalInfo.Runtime.GetDotNetRuntime();
			bool disposeFieldValue = true;
			var fieldValue = runtime.LoadField(evalInfo, nodeInfo.Value, fields.hasValueField);
			try {
				if (fieldValue.HasError || fieldValue.ValueIsException)
					return false;

				var rawValue = fieldValue.Value.GetRawValue();
				if (rawValue.ValueType != DbgSimpleValueType.Boolean)
					return false;
				if (!(bool)rawValue.RawValue) {
					nodeInfo.SetDisplayValue(new SyntheticNullValue(fields.valueField.FieldType));
					return true;
				}

				fieldValue.Value?.Dispose();
				fieldValue = default;

				fieldValue = runtime.LoadField(evalInfo, nodeInfo.Value, fields.valueField);
				if (fieldValue.HasError || fieldValue.ValueIsException)
					return false;

				nodeInfo.SetDisplayValue(fieldValue.Value);
				Create(evalInfo, providers, addParens, slotType, nodeInfo, evalOptions, createFlags | CreationOptions.NoNullable);
				disposeFieldValue = false;
				return true;
			}
			finally {
				if (disposeFieldValue)
					fieldValue.Value?.Dispose();
			}
		}

		void CreateCore(DbgEvaluationInfo evalInfo, List<DbgDotNetValueNodeProvider> providers, bool addParens, DmdType slotType, DbgDotNetValueNodeInfo nodeInfo, TypeState state, DbgValueNodeEvaluationOptions evalOptions, CreationOptions createFlags) {
			evalInfo.CancellationToken.ThrowIfCancellationRequested();
			if (state.HasNoChildren)
				return;

			if ((createFlags & CreationOptions.NoNullable) == 0 && state.IsNullable) {
				if (TryCreateNullable(evalInfo, providers, addParens, slotType, nodeInfo, state, evalOptions, createFlags))
					return;
			}

			if (state.Type.IsArray && !nodeInfo.Value.IsNull) {
				providers.Add(new ArrayValueNodeProvider(this, addParens, slotType, nodeInfo));
				return;
			}

			bool forceRawView = (evalOptions & DbgValueNodeEvaluationOptions.RawView) != 0;
			bool funcEval = (evalOptions & DbgValueNodeEvaluationOptions.NoFuncEval) == 0;

			if (state.IsTupleType && !forceRawView) {
				providers.Add(new TupleValueNodeProvider(addParens, slotType, nodeInfo, state.TupleFields));
				AddProvidersOneChildNode(providers, state, nodeInfo.Expression, addParens, slotType, nodeInfo.Value, evalOptions, isRawView: true);
				return;
			}

			if (!forceRawView && (createFlags & CreationOptions.NoProxy) == 0 && funcEval && !nodeInfo.Value.IsNull) {
				var proxyCtor = DebuggerTypeProxyFinder.GetDebuggerTypeProxyConstructor(state.Type);
				if ((object)proxyCtor != null) {
					var runtime = evalInfo.Runtime.GetDotNetRuntime();
					var proxyTypeResult = runtime.CreateInstance(evalInfo, proxyCtor, new[] { nodeInfo.Value }, DbgDotNetInvokeOptions.None);
					// Use the result even if the constructor threw an exception
					if (!proxyTypeResult.HasError) {
						var value = nodeInfo.Value;
						var origExpression = nodeInfo.Expression;
						nodeInfo.Expression = GetNewObjectExpression(proxyCtor, nodeInfo.Expression, slotType);
						nodeInfo.SetProxyValue(proxyTypeResult.Value);
						Create(evalInfo, providers, false, slotType, nodeInfo, evalOptions | DbgValueNodeEvaluationOptions.PublicMembers, createFlags | CreationOptions.NoProxy);
						AddProvidersOneChildNode(providers, state, origExpression, addParens, slotType, value, evalOptions, isRawView: true);
						return;
					}
				}
			}

			AddProviders(providers, state, nodeInfo.Expression, addParens, slotType, nodeInfo.Value, evalOptions, forceRawView);
		}

		DbgDotNetValueNodeProvider TryCreateDynamicView(TypeState state, string expression, DbgDotNetValue value, DmdType expectedType, DbgValueNodeEvaluationOptions evalOptions) {
			if (state.IsDynamicViewType && !value.IsNull)
				return new DynamicViewMembersValueNodeProvider(this, valueNodeFactory, value, expectedType, expression, state.Type.AppDomain, evalOptions);
			return null;
		}

		DbgDotNetValueNodeProvider TryCreateResultsView(TypeState state, string expression, DbgDotNetValue value, DmdType expectedType, DbgValueNodeEvaluationOptions evalOptions) {
			if ((object)state.EnumerableType != null && !value.IsNull)
				return new ResultsViewMembersValueNodeProvider(this, valueNodeFactory, state.EnumerableType, value, expectedType, expression, evalOptions);
			return null;
		}

		void AddProvidersOneChildNode(List<DbgDotNetValueNodeProvider> providers, TypeState state, string expression, bool addParens, DmdType slotType, DbgDotNetValue value, DbgValueNodeEvaluationOptions evalOptions, bool isRawView) {
			var tmpProviders = new List<DbgDotNetValueNodeProvider>(2);
			AddProviders(tmpProviders, state, expression, addParens, slotType, value, evalOptions, isRawView);
			if (tmpProviders.Count > 0)
				providers.Add(DbgDotNetValueNodeProvider.Create(tmpProviders));
		}

		internal void GetMemberCollections(DmdType type, DbgValueNodeEvaluationOptions evalOptions, out MemberValueNodeInfoCollection instanceMembersInfos, out MemberValueNodeInfoCollection staticMembersInfos) {
			var state = GetOrCreateTypeState(type);
			GetMemberCollections(state, evalOptions, out instanceMembersInfos, out staticMembersInfos);
		}

		void GetMemberCollections(TypeState state, DbgValueNodeEvaluationOptions evalOptions, out MemberValueNodeInfoCollection instanceMembersInfos, out MemberValueNodeInfoCollection staticMembersInfos) {
			lock (state) {
				if (state.CachedEvalOptions != evalOptions || state.CachedInstanceMembers.Members == null) {
					state.CachedEvalOptions = evalOptions;
					state.CachedInstanceMembers = Filter(state.InstanceMembers, evalOptions);
					state.CachedStaticMembers = Filter(state.StaticMembers, evalOptions);
				}
				instanceMembersInfos = state.CachedInstanceMembers;
				staticMembersInfos = state.CachedStaticMembers;
			}
		}

		void AddProviders(List<DbgDotNetValueNodeProvider> providers, TypeState state, string expression, bool addParens, DmdType slotType, DbgDotNetValue value, DbgValueNodeEvaluationOptions evalOptions, bool isRawView) {
			GetMemberCollections(state, evalOptions, out var instanceMembersInfos, out var staticMembersInfos);

			var membersEvalOptions = evalOptions;
			if (isRawView)
				membersEvalOptions |= DbgValueNodeEvaluationOptions.RawView;
			if (value.IsNull)
				instanceMembersInfos = MemberValueNodeInfoCollection.Empty;
			if (PointerValueNodeProvider.IsSupported(value))
				providers.Add(new PointerValueNodeProvider(this, expression, value));
			else {
				providers.Add(new InstanceMembersValueNodeProvider(valueNodeFactory, isRawView ? rawViewName : InstanceMembersName,
					expression, addParens, slotType, value, instanceMembersInfos, membersEvalOptions,
					isRawView ? PredefinedDbgValueNodeImageNames.RawView : PredefinedDbgValueNodeImageNames.InstanceMembers));
			}

			if (staticMembersInfos.Members.Length != 0)
				providers.Add(new StaticMembersValueNodeProvider(this, valueNodeFactory, StaticMembersName, state.TypeExpression, staticMembersInfos, membersEvalOptions));

			var provider = TryCreateResultsView(state, expression, value, slotType, evalOptions);
			if (provider != null)
				providers.Add(provider);
			provider = TryCreateDynamicView(state, expression, value, slotType, evalOptions);
			if (provider != null)
				providers.Add(provider);
		}
		static readonly DbgDotNetText rawViewName = new DbgDotNetText(new DbgDotNetTextPart(BoxedTextColor.Text, dnSpy_Roslyn_Shared_Resources.DebuggerVarsWindow_RawView));

		static MemberValueNodeInfoCollection Filter(in MemberValueNodeInfoCollection infos, DbgValueNodeEvaluationOptions evalOptions) {
			bool hideCompilerGeneratedMembers = (evalOptions & DbgValueNodeEvaluationOptions.HideCompilerGeneratedMembers) != 0;
			bool respectHideMemberAttributes = (evalOptions & DbgValueNodeEvaluationOptions.RespectHideMemberAttributes) != 0;
			bool publicMembers = (evalOptions & DbgValueNodeEvaluationOptions.PublicMembers) != 0;
			bool hideDeprecatedError = (evalOptions & DbgValueNodeEvaluationOptions.HideDeprecatedError) != 0;
			if (!hideCompilerGeneratedMembers && !respectHideMemberAttributes && !publicMembers && !hideDeprecatedError)
				return infos;
			bool hasHideRoot = false;
			var members = infos.Members.Where(a => {
				Debug.Assert(a.Member.MemberType == DmdMemberTypes.Field || a.Member.MemberType == DmdMemberTypes.Property);
				if (publicMembers && !a.IsPublic)
					return false;
				if (respectHideMemberAttributes && a.HasDebuggerBrowsableState_Never)
					return false;
				if (hideCompilerGeneratedMembers && a.IsCompilerGenerated)
					return false;
				if (hideDeprecatedError && a.DeprecatedError)
					return false;
				hasHideRoot |= a.HasDebuggerBrowsableState_RootHidden;
				return true;
			}).ToArray();
			return new MemberValueNodeInfoCollection(members, hasHideRoot);
		}
	}

	readonly struct DbgDotNetValueNodeProviderResult {
		public string ErrorMessage { get; }
		public DbgDotNetValueNodeProvider Provider { get; }
		public DbgDotNetValueNodeProviderResult(DbgDotNetValueNodeProvider provider) {
			ErrorMessage = null;
			Provider = provider;
		}
		public DbgDotNetValueNodeProviderResult(string errorMessage) {
			ErrorMessage = errorMessage ?? throw new ArgumentNullException(nameof(errorMessage));
			Provider = null;
		}
	}
}
