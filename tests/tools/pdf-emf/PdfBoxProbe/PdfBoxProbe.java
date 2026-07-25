// PDFBox によるプロットPDFの抽出プローブ（Phase 0 抽出検証・第3段 = 正解データ生成器）
// PDFGraphicsStreamEngine でパス・クリップ・画像を、PDFTextStripper(TextPosition) で文字を、
// グラフィックス状態（色・線幅・透過 ca/CA・レイヤ /OC・クリップ）ごと JSON へ書き出す。
// 座標はデバイス空間 = PDFユーザー空間（pt, y-up）。PdfPigProbe の JSON と突き合わせる。
// 使い方: java -cp "lib/pdfbox-app-3.0.7.jar;out" PdfBoxProbe <入力.pdf> <出力.json>
import java.awt.geom.Area;
import java.awt.geom.GeneralPath;
import java.awt.geom.Path2D;
import java.awt.geom.Point2D;
import java.awt.geom.Rectangle2D;
import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.Locale;

import org.apache.pdfbox.Loader;
import org.apache.pdfbox.contentstream.operator.Operator;
import org.apache.pdfbox.contentstream.PDFGraphicsStreamEngine;
import org.apache.pdfbox.cos.COSBase;
import org.apache.pdfbox.cos.COSDictionary;
import org.apache.pdfbox.cos.COSName;
import org.apache.pdfbox.pdmodel.PDDocument;
import org.apache.pdfbox.pdmodel.PDPage;
import org.apache.pdfbox.pdmodel.documentinterchange.markedcontent.PDPropertyList;
import org.apache.pdfbox.pdmodel.graphics.color.PDColor;
import org.apache.pdfbox.pdmodel.graphics.color.PDColorSpace;
import org.apache.pdfbox.pdmodel.graphics.image.PDImage;
import org.apache.pdfbox.pdmodel.graphics.image.PDImageXObject;
import org.apache.pdfbox.pdmodel.graphics.state.PDGraphicsState;
import org.apache.pdfbox.pdmodel.interactive.annotation.PDAnnotation;
import org.apache.pdfbox.text.PDFTextStripper;
import org.apache.pdfbox.text.TextPosition;
import org.apache.pdfbox.util.Matrix;

public final class PdfBoxProbe {

    /** JSON レコードの手書きビルダ（依存追加を避ける） */
    static final class Json {
        final StringBuilder sb = new StringBuilder();
        boolean first = true;
        Json() { sb.append('{'); }
        Json put(String k, Object v) {
            if (!first) sb.append(',');
            first = false;
            sb.append('"').append(k).append("\":").append(render(v));
            return this;
        }
        static String render(Object v) {
            if (v == null) return "null";
            if (v instanceof Boolean || v instanceof Integer || v instanceof Long) return v.toString();
            if (v instanceof Double d) {
                if (d.isNaN() || d.isInfinite()) return "null";
                return String.format(Locale.ROOT, "%.6g", d);
            }
            if (v instanceof Float f) return render((double) (float) f);
            if (v instanceof double[] a) {
                StringBuilder b = new StringBuilder("[");
                for (int i = 0; i < a.length; i++) { if (i > 0) b.append(','); b.append(render(a[i])); }
                return b.append(']').toString();
            }
            if (v instanceof float[] a) {
                StringBuilder b = new StringBuilder("[");
                for (int i = 0; i < a.length; i++) { if (i > 0) b.append(','); b.append(render((double) a[i])); }
                return b.append(']').toString();
            }
            return quote(v.toString());
        }
        static String quote(String s) {
            StringBuilder b = new StringBuilder("\"");
            for (int i = 0; i < s.length(); i++) {
                char c = s.charAt(i);
                switch (c) {
                    case '"' -> b.append("\\\"");
                    case '\\' -> b.append("\\\\");
                    case '\n' -> b.append("\\n");
                    case '\r' -> b.append("\\r");
                    case '\t' -> b.append("\\t");
                    default -> {
                        if (c < 0x20) b.append(String.format(Locale.ROOT, "\\u%04x", (int) c));
                        else b.append(c);
                    }
                }
            }
            return b.append('"').toString();
        }
        @Override public String toString() { return sb.toString() + "}"; }
    }

    static double[] toRgb(PDColor color) {
        if (color == null) return null;
        try {
            PDColorSpace cs = color.getColorSpace();
            if (cs == null) return null;
            float[] rgb = cs.toRGB(color.getComponents());
            return new double[] { rgb[0], rgb[1], rgb[2] };
        } catch (Exception e) {
            return null;
        }
    }

    /** CTM の等方スケール（√|det|）。線幅のユーザー空間→デバイス空間換算に使う */
    static double ctmScale(Matrix m) {
        double det = (double) m.getValue(0, 0) * m.getValue(1, 1)
                   - (double) m.getValue(0, 1) * m.getValue(1, 0);
        return Math.sqrt(Math.abs(det));
    }

    /** /OC マーク付き内容のレイヤ名解決を共有するミックスイン相当 */
    interface LayerTracker {
        Deque<String> layerStack();
        org.apache.pdfbox.pdmodel.PDResources currentResources();
        default void handleMarkedContent(Operator operator, List<COSBase> operands) {
            String name = operator.getName();
            if ("BDC".equals(name)) {
                String layer = null;
                if (operands.size() >= 2 && COSName.OC.equals(operands.get(0))) {
                    COSBase second = operands.get(1);
                    COSDictionary props = null;
                    if (second instanceof COSName ref) {
                        PDPropertyList pl = currentResources() == null ? null
                                : currentResources().getProperties(ref);
                        if (pl != null) props = pl.getCOSObject();
                    } else if (second instanceof COSDictionary d) {
                        props = d;
                    }
                    if (props != null) layer = props.getString(COSName.NAME);
                }
                layerStack().push(layer == null ? "" : layer);
            } else if ("BMC".equals(name)) {
                layerStack().push("");
            } else if ("EMC".equals(name)) {
                if (!layerStack().isEmpty()) layerStack().pop();
            }
        }
        default String currentLayer() {
            String top = layerStack().peek();
            return top == null || top.isEmpty() ? null : top;
        }
    }

    /** パス・クリップ・画像・シェーディングの収集エンジン */
    static final class GraphicsProbe extends PDFGraphicsStreamEngine implements LayerTracker {
        final List<String> records;
        final Deque<String> layers = new ArrayDeque<>();
        GeneralPath path = new GeneralPath();
        Point2D current = new Point2D.Double();
        int lines, curves, moves, closes;
        Integer pendingClip = null; // winding rule
        // bbox は制御点包含（PdfPig と同じ規約）で自前集計する。
        // Java 17+ の Path2D.getBounds2D() は曲線の厳密 bbox を返すため規約が食い違う
        double minX, minY, maxX, maxY;

        void updBounds(double x, double y) {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        GraphicsProbe(PDPage page, List<String> records) {
            super(page);
            this.records = records;
            resetPath();
        }
        @Override public Deque<String> layerStack() { return layers; }
        @Override public org.apache.pdfbox.pdmodel.PDResources currentResources() { return getResources(); }

        @Override
        protected void processOperator(Operator operator, List<COSBase> operands) throws IOException {
            handleMarkedContent(operator, operands);
            super.processOperator(operator, operands);
        }

        void resetPath() {
            path = new GeneralPath();
            lines = curves = moves = closes = 0;
            minX = minY = Double.MAX_VALUE;
            maxX = maxY = -Double.MAX_VALUE;
        }

        Json stateJson(String kind) {
            PDGraphicsState gs = getGraphicsState();
            Rectangle2D clip = gs.getCurrentClippingPath() == null ? null
                    : gs.getCurrentClippingPath().getBounds2D();
            double scale = ctmScale(gs.getCurrentTransformationMatrix());
            Json j = new Json();
            j.put("kind", kind)
             .put("bbox_pt", lines + curves + moves == 0 ? null
                     : new double[] { minX, minY, maxX, maxY })
             .put("lines", lines).put("curves", curves)
             .put("moves", moves).put("closes", closes)
             .put("lw_user", (double) gs.getLineWidth())
             .put("lw_dev_pt", gs.getLineWidth() * scale)
             .put("ctm_scale", scale)
             .put("ca", (double) gs.getNonStrokeAlphaConstant())
             .put("CA", (double) gs.getAlphaConstant())
             .put("layer", currentLayer())
             .put("clip_pt", clip == null ? null
                     : new double[] { clip.getMinX(), clip.getMinY(), clip.getMaxX(), clip.getMaxY() });
            return j;
        }

        void emitPaint(String kind, boolean fill, boolean stroke, Integer winding) {
            PDGraphicsState gs = getGraphicsState();
            Json j = stateJson("path");
            j.put("paint", kind)
             .put("filled", fill).put("stroked", stroke)
             .put("even_odd", winding != null && winding == Path2D.WIND_EVEN_ODD)
             .put("fill", fill ? toRgb(gs.getNonStrokingColor()) : null)
             .put("stroke", stroke ? toRgb(gs.getStrokingColor()) : null);
            records.add(j.toString());
        }

        void finishPath() {
            if (pendingClip != null) {
                Json j = stateJson("path");
                j.put("paint", "clip")
                 .put("filled", false).put("stroked", false)
                 .put("even_odd", pendingClip == Path2D.WIND_EVEN_ODD)
                 .put("fill", (Object) null).put("stroke", (Object) null);
                records.add(j.toString());
                path.setWindingRule(pendingClip);
                getGraphicsState().intersectClippingPath(path);
                pendingClip = null;
            }
            resetPath();
        }

        // ---- PDFGraphicsStreamEngine 抽象メソッド ----
        @Override public void appendRectangle(Point2D p0, Point2D p1, Point2D p2, Point2D p3) {
            path.moveTo(p0.getX(), p0.getY());
            path.lineTo(p1.getX(), p1.getY());
            path.lineTo(p2.getX(), p2.getY());
            path.lineTo(p3.getX(), p3.getY());
            path.closePath();
            updBounds(p0.getX(), p0.getY()); updBounds(p1.getX(), p1.getY());
            updBounds(p2.getX(), p2.getY()); updBounds(p3.getX(), p3.getY());
            moves++; lines += 3; closes++;
            current.setLocation(p0.getX(), p0.getY());
        }
        @Override public void drawImage(PDImage pdImage) {
            Matrix ctm = getGraphicsState().getCurrentTransformationMatrix();
            Point2D a = ctm.transformPoint(0, 0);
            Point2D b = ctm.transformPoint(1, 1);
            boolean smask = pdImage instanceof PDImageXObject xo
                    && xo.getCOSObject().containsKey(COSName.SMASK);
            Json j = new Json();
            j.put("kind", "image")
             .put("w_px", pdImage.getWidth()).put("h_px", pdImage.getHeight())
             .put("bbox_pt", new double[] {
                     Math.min(a.getX(), b.getX()), Math.min(a.getY(), b.getY()),
                     Math.max(a.getX(), b.getX()), Math.max(a.getY(), b.getY()) })
             .put("has_smask", smask)
             .put("ca", (double) getGraphicsState().getNonStrokeAlphaConstant())
             .put("layer", currentLayer());
            records.add(j.toString());
        }
        @Override public void clip(int windingRule) { pendingClip = windingRule; }
        @Override public void moveTo(float x, float y) {
            path.moveTo(x, y); moves++; updBounds(x, y); current.setLocation(x, y);
        }
        @Override public void lineTo(float x, float y) {
            path.lineTo(x, y); lines++; updBounds(x, y); current.setLocation(x, y);
        }
        @Override public void curveTo(float x1, float y1, float x2, float y2, float x3, float y3) {
            path.curveTo(x1, y1, x2, y2, x3, y3); curves++;
            updBounds(x1, y1); updBounds(x2, y2); updBounds(x3, y3);
            current.setLocation(x3, y3);
        }
        @Override public Point2D getCurrentPoint() { return current; }
        @Override public void closePath() { path.closePath(); closes++; }
        @Override public void endPath() { finishPath(); }
        @Override public void strokePath() { emitPaint("stroke", false, true, null); finishPath(); }
        @Override public void fillPath(int windingRule) { emitPaint("fill", true, false, windingRule); finishPath(); }
        @Override public void fillAndStrokePath(int windingRule) { emitPaint("fillstroke", true, true, windingRule); finishPath(); }
        @Override public void shadingFill(COSName shadingName) {
            Json j = stateJson("shading");
            j.put("name", shadingName.getName());
            records.add(j.toString());
        }
    }

    /** 文字（TextPosition = グリフ単位）の収集。色・透過・レイヤ付き */
    static final class TextProbe extends PDFTextStripper implements LayerTracker {
        final List<String> records;
        final Deque<String> layers = new ArrayDeque<>();
        TextProbe(List<String> records) throws IOException {
            this.records = records;
            setSortByPosition(false);
            setSuppressDuplicateOverlappingText(false);
            // PDFTextStripper 既定では色オペレータが未登録のため追加する
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetStrokingColorSpace(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetNonStrokingColorSpace(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetStrokingDeviceCMYKColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetNonStrokingDeviceCMYKColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetStrokingDeviceRGBColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetNonStrokingDeviceRGBColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetStrokingDeviceGrayColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetNonStrokingDeviceGrayColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetStrokingColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetStrokingColorN(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetNonStrokingColor(this));
            addOperator(new org.apache.pdfbox.contentstream.operator.color.SetNonStrokingColorN(this));
        }
        @Override public Deque<String> layerStack() { return layers; }
        @Override public org.apache.pdfbox.pdmodel.PDResources currentResources() { return getResources(); }

        @Override
        protected void processOperator(Operator operator, List<COSBase> operands) throws IOException {
            handleMarkedContent(operator, operands);
            super.processOperator(operator, operands);
        }

        @Override
        protected void processTextPosition(TextPosition text) {
            PDGraphicsState gs = getGraphicsState();
            Matrix tm = text.getTextMatrix();
            // TextPosition.getTextMatrix() はフォントサイズ折り込み済みの TRM（CTM 含む）。
            // getFontSizeInPt() は CTM を反映しないため、サイズは TRM のスケールから得る
            Json j = new Json();
            j.put("kind", "letter")
             .put("value", text.getUnicode())
             .put("font", text.getFont() == null ? null : text.getFont().getName())
             .put("size_pt", (double) tm.getScalingFactorY())
             .put("size_raw_pt", (double) text.getFontSizeInPt())
             .put("origin_pt", new double[] { tm.getTranslateX(), tm.getTranslateY() })
             .put("width_pt", (double) text.getWidthDirAdj())
             .put("h_scale", (double) (tm.getScalingFactorX() / tm.getScalingFactorY()))
             .put("color", toRgb(gs.getNonStrokingColor()))
             .put("ca", (double) gs.getNonStrokeAlphaConstant())
             .put("CA", (double) gs.getAlphaConstant())
             .put("layer", currentLayer());
            records.add(j.toString());
            super.processTextPosition(text);
        }
    }

    public static void main(String[] args) throws IOException {
        if (args.length < 2) {
            System.err.println("使い方: PdfBoxProbe <入力.pdf> <出力.json>");
            System.exit(1);
        }
        List<String> records = new ArrayList<>();
        try (PDDocument doc = Loader.loadPDF(new File(args[0]))) {
            PDPage page = doc.getPage(0);
            System.out.printf(Locale.ROOT, "page size: %s x %s pt%n",
                    page.getMediaBox().getWidth(), page.getMediaBox().getHeight());

            new GraphicsProbe(page, records).processPage(page);
            int nPaths = records.size();

            TextProbe tp = new TextProbe(records);
            tp.setStartPage(1);
            tp.setEndPage(1);
            tp.getText(doc);
            int nLetters = records.size() - nPaths;

            for (PDAnnotation a : page.getAnnotations()) {
                Json j = new Json();
                j.put("kind", "annotation")
                 .put("type", a.getSubtype())
                 .put("content", a.getContents())
                 .put("bbox_pt", a.getRectangle() == null ? null : new double[] {
                         a.getRectangle().getLowerLeftX(), a.getRectangle().getLowerLeftY(),
                         a.getRectangle().getUpperRightX(), a.getRectangle().getUpperRightY() });
                records.add(j.toString());
            }
            System.out.printf(Locale.ROOT, "paths+etc=%d letters=%d annots=%d%n",
                    nPaths, nLetters, records.size() - nPaths - nLetters);
        }
        Files.write(Paths.get(args[1]),
                ("[" + String.join(",\n", records) + "]").getBytes(StandardCharsets.UTF_8));
        System.out.println("JSON出力: " + args[1] + " (" + records.size() + "件)");
    }
}
