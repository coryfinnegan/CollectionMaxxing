// Jest stand-in for lottie-react-native: the animation is native; tests assert it was
// asked for (by testID and source), not how it moved.
const React = require('react');
const { View } = require('react-native');

const LottieView = React.forwardRef((props, ref) => {
  const { testID = 'lottie', ...rest } = props;
  return React.createElement(View, { ...rest, testID, ref });
});
LottieView.displayName = 'LottieView';

module.exports = { __esModule: true, default: LottieView, LottieView };
