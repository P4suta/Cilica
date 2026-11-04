namespace Cilica.PracticeTests;

using Cilica.Attributes;

[Optimize]
public class RegAllocTests
{
    [Theory]
    [InlineData("-", "0")]
    [InlineData("OFF", "0")]
    [InlineData("Off", "0")]
    [InlineData("on", "on")]
    public void SharedSelectLocalsPreserveBothPaths(string value, string expected)
    {
        if (value == "-" || value.ToUpperInvariant() == "OFF") value = "0";
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(0, 1020)]
    [InlineData(1, 2000)]
    [InlineData(2, 0)]
    public void ParallelCopiesPreserveRegisterSourcesBeforeConstants(int n, int expected)
    {
        int a = 10, b = 20;
        for (int i = 0; i < n; i++) { a = b; b = 0; }
        Assert.Equal(expected, a * 100 + b);
    }

    [Fact]
    public void LiveReg_Loop_Finally()
    {
        long a = 0, b = 0, c = 0;

        for (int i = 0; i < 100 && c < 100; i++, c++) {
            try {
                a += i;
            } finally {
                int tmp = (int)(a % 5);  // buggy regalloc will reuse `i`
                if (tmp == 1 || tmp == 3) {
                    b += a;
                }
            }
        }
        Assert.Equal(4950, a);
        Assert.Equal(99950, b);
    }
}
