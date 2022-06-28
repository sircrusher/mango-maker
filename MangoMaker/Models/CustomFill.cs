using Solnet.Mango.Models.Events;
using System;

namespace MangoMaker.Models
{
	public class CustomFill
	{
		public FillEvent Fill { get; set; }
		public bool Hedged { get; set; }
		public DateTime HedgeConfirmedTimestamp { get; set; }
		public DateTime FillNotifiedTimestamp { get; set; }
		public decimal HedgedPrice { get; set; }
	}
}