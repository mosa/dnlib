﻿#pragma warning disable 0169	//TODO:

namespace dot10.dotNET.Types {
	/// <summary>
	/// A high-level representation of a row in the Event table
	/// </summary>
	public abstract class EventDef : IHasCustomAttribute, IHasSemantic {
		/// <summary>
		/// The row id in its table
		/// </summary>
		protected uint rid;

		/// <summary>
		/// From column Event.EventFlags
		/// </summary>
		ushort eventFlags;

		/// <summary>
		/// From column Event.Name
		/// </summary>
		string name;

		/// <summary>
		/// From column Event.EventType
		/// </summary>
		ITypeDefOrRef eventType;

		/// <inheritdoc/>
		public MDToken MDToken {
			get { return new MDToken(Table.Event, rid); }
		}

		/// <inheritdoc/>
		public int HasCustomAttributeTag {
			get { return 10; }
		}

		/// <inheritdoc/>
		public int HasSemanticTag {
			get { return 0; }
		}
	}

	/// <summary>
	/// A Event row created by the user and not present in the original .NET file
	/// </summary>
	public class EventDefUser : EventDef {
	}
}
