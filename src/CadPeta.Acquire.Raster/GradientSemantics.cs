namespace CadPeta.Acquire.Raster;

/// <summary>AutoCAD 2026 のPDF印刷経路正解チャートから固定したグラデーション空間関数。</summary>
public static class GradientSemantics
{
    // 生成元: tests/assets/testcharts/gradient/charts/*.pdf。
    // 再生成: tests/tools/pdf-emf/gradient_fit/GradientFit.csproj の profiles モード。
    // JPEG±2を左右/縦方向の中央値で吸収した、等間隔の8bit実測節点である。
    const double CenteredRadialNodeSpacing = .1;
    static readonly double[] CenteredRadialSurface =
        [.996078, .976471, .945098, .882353, .788235, .682353,
         .568627, .447059, .298039, .149020, 0];

    const double NonCenteredProfileNodeSpacing = .05;
    static readonly double[] NonCenteredLinearProfile =
        [0, .082353, .164706, .247059, .325490, .396078, .470588,
         .533333, .568627, .603922, .639216, .674510, .709804,
         .745098, .780392, .811765, .847059, .882353, .917647,
         .952941, .988235];
    static readonly double[] NonCenteredCylinderProfile =
        [0, .250980, .490196, .698039, .847059, .964706, .984314,
         .988235, .956863, .929412, .901961, .839216, .776471,
         .709804, .627451, .537255, .447059, .349020, .243137,
         .137255, .031373];

    // 非Centeredの2次元テンプレート。0°/45°の透明画素を除外して次数6の
    // Chebyshev基底へ最小二乗し、別採取した90°で交差検証した。
    // 再生成: GradientFit の shiftedfit モード。並びは y次数優先の7x7。
    const int NonCenteredTensorDegree = 6;
    static readonly double[] NonCenteredHemisphericalTensor =
        [.31837472343831, -.3216965291267809, -.09904538345052043, .029136841577093953, .016201036887346615, .015211773682354476, .026221314353078822,
         .3653881742043598, -.2530993239357896, .0056939825701978015, .11041484533546331, .012443642799108872, -.016377915744479426, .018495688403199848,
         -.06440061323615527, .06108806921593531, -.10313961559705943, -.008561625257636846, .019902037626528285, .029161316362827424, .045680884191944805,
         .007493853431186182, .008129998341584635, .06536534463774431, .07273042777275163, -.001794734826882068, -.01709958047373765, .01067058378385698,
         -.016989943445002254, -.004697317058531561, -.05687827060792894, .017420578761360123, .023814231660084187, .021876311966862082, .02131182052459235,
         .01065821123609221, -.0025025612722383455, .012629192849505904, .010728872476114638, -.002751830014457341, -.001073457204292568, .011857874705434444,
         -.01130059994964985, -.0015938920729933377, -.016629555653693576, .006974167869677267, .004961094931687078, .00030635559954184403, .0042677722270149515];
    static readonly double[] NonCenteredCurvedTensor =
        [.4895865978103866, -.1317784830287531, -.05498557791829779, .014048396032657368, .00982398981707009, .0027230964596181434, .0017202388181367587,
         .4527339163895482, -.08347037007391783, -.05592850491038179, -.020262639821788747, -.015911819844710254, -.006247916307132943, .0032216578225320557,
         -.06718760436653833, .048145109635359284, .03613564508789213, .016976931516796384, .015527454761889381, .005265490315426731, .0026372077260323097,
         -.031553207543108086, -.027567625184045333, -.022734812717122715, -.01427549895975963, -.011042828694188822, -.005542262073694293, .0024093206228642285,
         .015062052393524497, .021062734240082537, .014846294185889772, .007667700512698897, .0087933732998495, .00332161698955075, .002837389022686272,
         -.003835318423549134, -.010157127981812564, -.005188203089361264, -.005810647180981808, -.0036527085644140825, -.0027347850921451335, .0004913389511623251,
         .00392018180121017, .0068183673795193835, .004467908175068841, .002405323222546083, .004204968660770301, .0003849447481786908, .003394718182212924];

    const double ShiftedSphereCenterFraction = .25;
    const double HemisphericalLightDistanceInDiameters = .5;
    const double CurvedAlongToCrossRatio = 1.0 / 3.0;

    public static bool IsKnown(string name) => Normalize(name) is
        "LINEAR" or "CYLINDER" or "INVCYLINDER" or "SPHERICAL" or "INVSPHERICAL" or
        "HEMISPHERICAL" or "INVHEMISPHERICAL" or "CURVED" or "INVCURVED";

    public static double Evaluate(string name, double x, double y,
                                  double minX, double minY, double maxX, double maxY,
                                  double angleRadians, double shift)
    {
        name = Normalize(name);
        double width = maxX - minX, height = maxY - minY;
        if (!(width > 0) || !(height > 0) || !Finite(angleRadians)
            || !Finite(shift) || shift < 0 || shift > 1)
            return double.NaN;

        // AutoCAD 2026 はDXF 461の0.5と1を画素単位で同一描画する。
        // したがって連続補間ではなくCentered/非Centeredの二値として扱う。
        bool centered = shift == 0;
        double dx = x - (minX + maxX) / 2;
        double dy = y - (minY + maxY) / 2;
        Axes axes = MakeAxes(width, height, angleRadians);
        double along = (dx * axes.Cos - dy * axes.Sin) / axes.AlongSpan;

        double value = name switch
        {
            "LINEAR" => centered ? Clamp01(along + .5)
                                 : ProfileAt(NonCenteredLinearProfile, along + .5,
                                             NonCenteredProfileNodeSpacing),
            "CYLINDER" => Cylinder(along, centered),
            "INVCYLINDER" => 1 - Cylinder(along, centered),
            "SPHERICAL" => Sphere(dx, dy, axes, centered),
            "INVSPHERICAL" => 1 - Sphere(dx, dy, axes, centered),
            "HEMISPHERICAL" => Hemisphere(dx, dy, width, height, angleRadians, centered),
            "INVHEMISPHERICAL" => 1 - Hemisphere(dx, dy, width, height, angleRadians, centered),
            "CURVED" => Curved(dx, dy, width, height, angleRadians, centered),
            "INVCURVED" => 1 - Curved(dx, dy, width, height, angleRadians, centered),
            _ => double.NaN,
        };
        return Clamp01(value);
    }

    static double Cylinder(double along, bool centered) => centered
        ? RadialSurfaceAt(Math.Abs(2 * along))
        : ProfileAt(NonCenteredCylinderProfile, along + .5, NonCenteredProfileNodeSpacing);

    static double Sphere(double dx, double dy, Axes axes, bool centered)
    {
        double diameter = Math.Max(axes.AlongSpan, axes.CrossSpan);
        if (!centered)
        {
            dx += (axes.Cos + axes.Sin) * diameter * ShiftedSphereCenterFraction;
            dy += (-axes.Sin + axes.Cos) * diameter * ShiftedSphereCenterFraction;
        }
        return RadialSurfaceAt(2 * Math.Sqrt(dx * dx + dy * dy) / diameter);
    }

    static double Hemisphere(double dx, double dy, double width, double height,
                             double angle, bool centered)
    {
        Axes axes = MakeAxes(width, height, angle);
        double diameter = Math.Max(axes.AlongSpan, axes.CrossSpan);
        if (!centered)
        {
            double u = .5 + (dx * axes.Cos - dy * axes.Sin) / diameter;
            double v = .5 + (dx * axes.Sin + dy * axes.Cos) / diameter;
            return EvaluateTensor(NonCenteredHemisphericalTensor, u, v);
        }
        double centerX = axes.Sin * diameter * HemisphericalLightDistanceInDiameters;
        double centerY = axes.Cos * diameter * HemisphericalLightDistanceInDiameters;
        return RadialSurfaceAt(Math.Sqrt((dx - centerX) * (dx - centerX)
                                       + (dy - centerY) * (dy - centerY)) / diameter);
    }

    static double Curved(double dx, double dy, double width, double height,
                         double angle, bool centered)
    {
        Axes axes = MakeAxes(width, height, angle);
        double along = (dx * axes.Cos - dy * axes.Sin) / axes.AlongSpan;
        double cross = (dx * axes.Sin + dy * axes.Cos) / axes.CrossSpan;
        if (!centered)
            return EvaluateTensor(NonCenteredCurvedTensor, along + .5, cross + .5);
        double radius = Math.Sqrt(Math.Pow(.5 - cross, 2)
                                + Math.Pow(CurvedAlongToCrossRatio * 2 * along, 2));
        return RadialSurfaceAt(radius);
    }

    static Axes MakeAxes(double width, double height, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new Axes(c, s,
            Math.Abs(c) * width + Math.Abs(s) * height,
            Math.Abs(s) * width + Math.Abs(c) * height);
    }

    static double RadialSurfaceAt(double radius) =>
        ProfileAt(CenteredRadialSurface, radius, CenteredRadialNodeSpacing);

    static double ProfileAt(double[] nodes, double argument, double spacing)
    {
        if (argument <= 0) return nodes[0];
        double lastArgument = spacing * (nodes.Length - 1);
        if (argument >= lastArgument) return nodes[nodes.Length - 1];
        double position = argument / spacing;
        int index = (int)position;
        double fraction = position - index;
        return nodes[index] + (nodes[index + 1] - nodes[index]) * fraction;
    }

    static double EvaluateTensor(double[] coefficients, double u, double v)
    {
        double[] x = Chebyshev(2 * u - 1), y = Chebyshev(2 * v - 1);
        int side = NonCenteredTensorDegree + 1;
        double value = 0;
        for (int row = 0; row < side; row++)
        for (int column = 0; column < side; column++)
            value += coefficients[row * side + column] * x[column] * y[row];
        return value;
    }

    static double[] Chebyshev(double argument)
    {
        var values = new double[NonCenteredTensorDegree + 1];
        values[0] = 1;
        values[1] = argument;
        for (int i = 2; i < values.Length; i++)
            values[i] = 2 * argument * values[i - 1] - values[i - 2];
        return values;
    }

    static string Normalize(string name) => (name ?? "").Trim().ToUpperInvariant();
    static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    readonly record struct Axes(double Cos, double Sin, double AlongSpan, double CrossSpan);
}
