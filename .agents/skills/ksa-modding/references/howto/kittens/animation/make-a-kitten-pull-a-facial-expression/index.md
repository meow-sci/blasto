# Game Feature

* [Make a kitten pull a facial expression](overview.md) - How the five authored faces are mixed over a kitten's pose by CatExpressionAnim processors, why writing the game's reactive processor never holds, and two ways to drive faces and the eye, ear and personality weights

# How-To

* [Own a CatExpressionAnim appended after the game's four, and bust its pose cache on every swap](make-a-kitten-pull-a-facial-expression-1.md) - Kitten Animations adds a CatExpressionAnim of its own to the model's AnimProcessors so it mixes last over every game pose, sets its ExpressionAnim and weight from an ease-in, hold and ease-out envelope, and nulls the private _expressionPose when the clip changes
* [Re-apply eye, ear and personality weights each frame, cap the reactive face with Math.Min, and restore the originals on unbind](make-a-kitten-pull-a-facial-expression-2.md) - The same UpdateAnimation prefix that forces clips writes the ear weight, eye look angle, eye pitch offset and personality weight after the game's own writes, caps the acceleration-driven face with Math.Min and puts the bind-time values back when the kitten is released
