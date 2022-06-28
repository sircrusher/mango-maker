using Solnet.Serum.Models;
using System;
using System.Collections.Generic;

namespace MangoMaker
{
	static class Extensions
	{
        public static Side OtherSide(this Side side)
		{
			return side == Side.Buy ? Side.Sell : Side.Buy;
		}

		public static IEnumerable<T> IfThenElse<T>(
			this IEnumerable<T> elements,
			Func<bool> condition,
			Func<IEnumerable<T>, IEnumerable<T>> thenPath,
			Func<IEnumerable<T>, IEnumerable<T>> elsePath)
		{
			return condition()
				? thenPath(elements)
				: elsePath(elements);
		}

		public static bool AreAllSame<T>(this IEnumerable<T> enumerable)
		{
			if (enumerable == null) throw new ArgumentNullException(nameof(enumerable));

			using (var enumerator = enumerable.GetEnumerator())
			{
				var toCompare = default(T);
				if (enumerator.MoveNext())
				{
					toCompare = enumerator.Current;
				}

				while (enumerator.MoveNext())
				{
					if (toCompare != null && !toCompare.Equals(enumerator.Current))
					{
						return false;
					}
				}
			}

			return true;
		}
    }
}