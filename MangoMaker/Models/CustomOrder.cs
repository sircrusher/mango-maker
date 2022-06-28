using Solnet.Serum.Models;

namespace MangoMaker.Models
{
	public class CustomOrder : Solnet.Mango.Models.Matching.OpenOrder
	{
		public Side Side { get; set; }

		public bool ReduceOnly { get; set; }
	}
}