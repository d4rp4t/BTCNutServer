import webpack from 'webpack';

export default {
  entry: './AnimatedToken.js',
  output: {
    filename: 'bundle.js',
  },
  target: 'web',
  plugins: [
    new webpack.ProvidePlugin({
      process: 'process/browser.js',
    }),
    new webpack.ProvidePlugin({
      Buffer: ['buffer', 'Buffer'],
    }),
  ],
};