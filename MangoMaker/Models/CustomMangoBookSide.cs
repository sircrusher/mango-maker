using Solnet.Mango.Models.Matching;
using System;
using System.Collections.Generic;

namespace MangoMaker.Models
{
	public class CustomMangoBookSide
	{
		public List<OpenOrder> OpenOrders { get; set; }

		public DateTime LastUpdated { get; set; }
	}
}