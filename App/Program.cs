using System.Threading;

namespace ImplicateX.TinyCLR.Drivers.Decoder
{
	internal class Program
	{
		private static Board board = null!;

		static void Main()
		{
			board = new Board();

			Thread.Sleep( Timeout.Infinite );
		}
	}
}
