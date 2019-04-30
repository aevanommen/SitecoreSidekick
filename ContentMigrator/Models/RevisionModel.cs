using System;
using System.Collections.Generic;

namespace ScsContentMigrator.Models
{
	public class RevisionModel
	{
		public string Id;
		public Dictionary<Guid, string> Rev;
		public List<Guid> IdsAndChildrenToExclude;
	}
}
