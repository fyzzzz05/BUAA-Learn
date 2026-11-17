import SwiftUI

/// Preserve the reading order while wrapping actions to the available panel width.
struct LessonActionFlow: Layout {
    var spacing: CGFloat = 8
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrangement(width:proposal.width ?? .greatestFiniteMagnitude,subviews:subviews).size
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let layout = arrangement(width:bounds.width,subviews:subviews)
        for (index,point) in layout.points.enumerated() {
            subviews[index].place(at:CGPoint(x:bounds.minX + point.x,y:bounds.minY + point.y),anchor:.topLeading,proposal:.unspecified)
        }
    }
    private func arrangement(width:CGFloat,subviews:Subviews) -> (size:CGSize,points:[CGPoint]) {
        var x:CGFloat = 0, y:CGFloat = 0, lineHeight:CGFloat = 0, usedWidth:CGFloat = 0
        var points:[CGPoint] = []
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0 && x + size.width > width { y += lineHeight + spacing; x = 0; lineHeight = 0 }
            points.append(CGPoint(x:x,y:y))
            usedWidth = max(usedWidth,x + size.width)
            x += size.width + spacing; lineHeight = max(lineHeight,size.height)
        }
        return (CGSize(width:usedWidth,height:y + lineHeight),points)
    }
}
