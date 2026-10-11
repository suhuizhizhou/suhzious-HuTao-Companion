using System.Globalization;
using System.Text.RegularExpressions;

namespace HuTao.Foundation.Computation;

public static class SafeArithmetic
{
    public static double Calculate(double left,string operation,double right)
    {
        if(!double.IsFinite(left) || !double.IsFinite(right))throw new ArithmeticException("non_finite_operand");
        var value=operation switch
        {
            "add"=>left+right,"subtract"=>left-right,"multiply"=>left*right,
            "divide" when right!=0=>left/right,"remainder" when right!=0=>left%right,
            "power"=>Math.Pow(left,right),_=>throw new ArgumentException("division_by_zero_or_unknown_operation")
        };
        if(!double.IsFinite(value))throw new ArithmeticException("non_finite_result");
        return value;
    }

    /// <summary>Only a whole, unqualified numeric equation. Never partially evaluate prose,
    /// quoted examples, questions, approximate answers, compound expressions or scripts.</summary>
    public static bool IsIncorrectEquation(string text)
    {
        if(text.Length>300)return false;
        const string number=@"[+-]?\d+(?:\.\d+)?";
        var match=Regex.Match(text,@"^\s*(?<left>"+number+@")\s*(?<op>乘以|乘|×|\*|加上|加|\+|减去|减|-|除以|÷|/)\s*(?<right>"+number+
            @")\s*(?:等于|是|=|＝)\s*(?<result>"+number+@")[。.!！\s]*$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
        if(!match.Success)return false;
        if(!double.TryParse(match.Groups["left"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var left) ||
            !double.TryParse(match.Groups["right"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var right) ||
            !double.TryParse(match.Groups["result"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var claimed) ||
            !double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(claimed))return false;
        // This is a floating-point calculator, not arbitrary-precision symbolic arithmetic.
        if(Math.Abs(left)>1000000 || Math.Abs(right)>1000000)return false;
        var operation=match.Groups["op"].Value switch
        {"乘以" or "乘" or "×" or "*"=>"multiply","加上" or "加" or "+"=>"add","减去" or "减" or "-"=>"subtract",_=>"divide"};
        if(operation=="divide" && right==0)return true;
        var actual=Calculate(left,operation,right);
        var tolerance=Math.Max(1e-12,4*Math.Abs(Math.BitIncrement(actual)-actual));
        return Math.Abs(actual-claimed)>tolerance;
    }
}
