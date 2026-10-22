using CallReceiver.Models;
using Xunit;

namespace CallReceiver.Tests;

public sealed class PhoneTextTests
{
    [Theory]
    [InlineData("01012345678", "010-1234-5678")]
    [InlineData("0111234567", "011-123-4567")]
    [InlineData("01112345678", "011-1234-5678")]
    [InlineData("0161234567", "016-123-4567")]
    [InlineData("01612345678", "016-1234-5678")]
    [InlineData("0171234567", "017-123-4567")]
    [InlineData("01712345678", "017-1234-5678")]
    [InlineData("0181234567", "018-123-4567")]
    [InlineData("01812345678", "018-1234-5678")]
    [InlineData("0191234567", "019-123-4567")]
    [InlineData("01912345678", "019-1234-5678")]
    [InlineData("021234567", "02-123-4567")]
    [InlineData("0212345678", "02-1234-5678")]
    [InlineData("07012345678", "070-1234-5678")]
    [InlineData("05012345678", "050-1234-5678")]
    [InlineData("050712345678", "0507-1234-5678")]
    [InlineData("0601234567", "060-123-4567")]
    [InlineData("0801234567", "080-123-4567")]
    [InlineData("15881588", "1588-1588")]
    [InlineData("16441234", "1644-1234")]
    [InlineData("18001234", "1800-1234")]
    public void DomesticBodyUsesHyphensButCaptionAndEventKeepOriginal(string input, string expected)
    {
        var call = CallEvent.Test() with { PhoneNumber = input };
        var settings = new AppSettings { TitleItem1 = "Phone", TitleItem2 = "None", TitleItem3 = "None" };
        Assert.Equal(expected, PhoneText.Format(call.PhoneNumber));
        Assert.Equal(expected, PhoneText.Format(expected));
        Assert.Equal(input, PopupText.Title(call, settings));
        Assert.Equal(input, call.PhoneNumber);
    }

    [Theory]
    [InlineData("031")]
    [InlineData("032")]
    [InlineData("033")]
    [InlineData("041")]
    [InlineData("042")]
    [InlineData("043")]
    [InlineData("044")]
    [InlineData("051")]
    [InlineData("052")]
    [InlineData("053")]
    [InlineData("054")]
    [InlineData("055")]
    [InlineData("061")]
    [InlineData("062")]
    [InlineData("063")]
    [InlineData("064")]
    public void KnownRegionsSupportBothSubscriberLengths(string prefix)
    {
        Assert.Equal($"{prefix}-123-4567", PhoneText.Format(prefix + "1234567"));
        Assert.Equal($"{prefix}-1234-5678", PhoneText.Format(prefix + "12345678"));
        Assert.Equal(prefix + "123456", PhoneText.Format(prefix + "123456"));
        Assert.Equal(prefix + "123456789", PhoneText.Format(prefix + "123456789"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("PRIVATE")]
    [InlineData("112")]
    [InlineData("119")]
    [InlineData("12345678")]
    [InlineData("010-1234-5678")]
    [InlineData("1588-1588")]
    [InlineData("+821012345678")]
    [InlineData("00821012345678")]
    [InlineData(" 01012345678")]
    [InlineData("01012345678 ")]
    [InlineData("010 1234 5678")]
    [InlineData("0101234567A")]
    [InlineData("０１０１２３４５６７８")]
    [InlineData("٠١٠١٢٣٤٥٦٧٨")]
    [InlineData("0101234567")]
    [InlineData("010123456789")]
    [InlineData("02123456")]
    [InlineData("02123456789")]
    [InlineData("011123456")]
    [InlineData("011123456789")]
    [InlineData("0701234567")]
    [InlineData("070123456789")]
    [InlineData("0501234567")]
    [InlineData("0501234567890")]
    [InlineData("060123456")]
    [InlineData("06012345678")]
    [InlineData("080123456")]
    [InlineData("08012345678")]
    [InlineData("1588158")]
    [InlineData("158815888")]
    [InlineData("14441234")]
    [InlineData("17001234")]
    [InlineData("01512345678")]
    [InlineData("0341234567")]
    [InlineData("09912345678")]
    public void UnsupportedAndAlreadyFormattedValuesRemainUnchanged(string input) =>
        Assert.Equal(input, PhoneText.Format(input));

    [Fact] public void ExistingMaskingHelperRemainsUnchanged() =>
        Assert.Equal("010-****-5678", PhoneText.Mask("01012345678"));
}
